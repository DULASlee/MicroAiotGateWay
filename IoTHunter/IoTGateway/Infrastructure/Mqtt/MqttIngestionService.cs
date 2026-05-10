using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IoTHunter.Shared.Domain;
using IoTHunter.Shared.Infrastructure;
using IoTGateway.Contracts;
using IoTGateway.Infrastructure.Messaging;
using IoTGateway.Infrastructure.Metrics;
using IoTGateway.Infrastructure.Options;
using MQTTnet;
using MQTTnet.Protocol;

namespace IoTGateway.Infrastructure.Mqtt;

internal sealed partial class MqttIngestionService : BackgroundService
{
    [GeneratedRegex(@"^device/(?<deviceId>[^/]+)/(?<rest>telemetry|event/critical)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex TopicPattern();

    private readonly KafkaProducerService _producer;
    private readonly MqttOptions _mqttOptions;
    private readonly GatewayMetrics _metrics;
    private readonly ILogger<MqttIngestionService> _logger;
    private IMqttClient? _client;
    private CancellationToken _stoppingToken;

    public MqttIngestionService(
        KafkaProducerService producer,
        MqttOptions mqttOptions,
        GatewayMetrics metrics,
        ILogger<MqttIngestionService> logger)
    {
        _producer = producer;
        _mqttOptions = mqttOptions;
        _metrics = metrics;
        _logger = logger;
    }

    public bool IsConnected => _client?.IsConnected ?? false;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        var factory = new MqttClientFactory();
        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            attempt++;
            var client = factory.CreateMqttClient();
            var disconnectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            client.DisconnectedAsync += e =>
            {
                if (e.ClientWasConnected)
                {
                    _logger.LogWarning("MQTT disconnected: {Reason}", e.Reason);
                    disconnectTcs.TrySetResult(true);
                }
                return Task.CompletedTask;
            };

            client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;

            try
            {
                await ConnectAndSubscribeAsync(client, stoppingToken);
                _client = client;
                attempt = 0; // reset backoff counter on successful connection
                _logger.LogInformation("MQTT ingestion started, TCP={Server}:{Port}",
                    _mqttOptions.TcpServer, _mqttOptions.TcpPort);

                // Wait until disconnect or shutdown — TCS gives instant wake-up on disconnect
                var completed = await Task.WhenAny(disconnectTcs.Task, Task.Delay(Timeout.Infinite, stoppingToken));
                if (completed == disconnectTcs.Task)
                {
                    _logger.LogWarning("MQTT disconnected, reconnecting...");
                }
                await DisposeClientAsync(client);
                _client = null;

                if (stoppingToken.IsCancellationRequested) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await DisposeClientAsync(client);
                break;
            }
            catch (Exception ex)
            {
                var delayMs = _mqttOptions.RetryBaseDelayMs * (1 << Math.Min(attempt - 1, 5));
                _logger.LogError(ex,
                    "MQTT connection failed (attempt {Attempt}), retrying in {DelayMs}ms",
                    attempt, delayMs);
                await DisposeClientAsync(client);
                try { await Task.Delay(delayMs, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("MQTT ingestion stopping...");
        if (_client is not null)
        {
            await DisposeClientAsync(_client);
        }
    }

    private static async Task DisposeClientAsync(IMqttClient client)
    {
        try { await client.DisconnectAsync(); } catch { }
        client.Dispose();
    }

    private async Task ConnectAndSubscribeAsync(IMqttClient client, CancellationToken ct)
    {
        var clientOptionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(_mqttOptions.TcpServer, _mqttOptions.TcpPort)
            .WithClientId($"{_mqttOptions.ClientId}-{Random.Shared.Next(1000, 9999)}")
            .WithCleanSession(false)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));

        if (!string.IsNullOrWhiteSpace(_mqttOptions.Username))
        {
            clientOptionsBuilder.WithCredentials(_mqttOptions.Username, _mqttOptions.Password);
        }

        await client.ConnectAsync(clientOptionsBuilder.Build(), ct);

        await client.SubscribeAsync(new MqttTopicFilterBuilder()
            .WithTopic("$share/gateway-group/device/+/telemetry")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .Build(), ct);

        await client.SubscribeAsync(new MqttTopicFilterBuilder()
            .WithTopic("$share/gateway-group/device/+/event/critical")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);

        _logger.LogInformation("MQTT connected and subscribed");
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

        try
        {
            var match = TopicPattern().Match(topic);
            if (!match.Success)
            {
                _logger.LogWarning("MQTT rejected: invalid topic '{Topic}'", topic);
                _metrics.RecordRejection("mqtt", "invalid_topic");
                return;
            }

            var deviceId = match.Groups["deviceId"].Value;
            var isCritical = match.Groups["rest"].Value == "event/critical";

            var request = JsonSerializer.Deserialize<TelemetryRequest>(payload, SerializerSetup.TightOptions);
            if (request is null)
            {
                _logger.LogWarning("MQTT rejected: deserialization failed");
                _metrics.RecordRejection("mqtt", "deserialization_failed");
                return;
            }

            var validationResults = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
            var validationContext = new System.ComponentModel.DataAnnotations.ValidationContext(request);
            if (!System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
                    request, validationContext, validationResults, validateAllProperties: true))
            {
                _logger.LogWarning("MQTT rejected: validation failed {Errors}",
                    string.Join("; ", validationResults));
                _metrics.RecordRejection("mqtt", "validation_failed");
                return;
            }

            var reliability = isCritical ? ReliabilityLevel.Critical : ReliabilityLevel.BestEffort;
            var kafkaTopic = isCritical
                ? ReliabilityConfiguration.KafkaTopics[ReliabilityLevel.Critical]
                : ReliabilityConfiguration.KafkaTopics[ReliabilityLevel.BestEffort];

            var envelope = TelemetryEnvelopeMapper.ToEnvelope(request, reliability);
            await _producer.ProduceAsync(kafkaTopic, envelope, _stoppingToken);

            _metrics.RecordRequest("mqtt", kafkaTopic, "success");
            _metrics.RecordMqttMessage(topic, isCritical ? "qos1" : "qos0");

            _logger.LogInformation(
                "MQTT→Kafka {EventId} device={DeviceId} topic={MqttTopic}→{KafkaTopic} reliability={Level}",
                envelope.EventId, deviceId, topic, kafkaTopic, reliability);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQTT processing failed topic={Topic}", topic);
            _metrics.RecordRejection("mqtt", ex.GetType().Name);
        }
    }
}
