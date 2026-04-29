using System.ComponentModel.DataAnnotations;
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
    [GeneratedRegex(
        @"^device/(?<deviceId>[^/]+)/(?<rest>telemetry|event/critical)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex TopicPattern();

    private readonly KafkaProducerService _producer;
    private readonly MqttOptions _mqttOptions;
    private readonly GatewayMetrics _metrics;
    private readonly ILogger<MqttIngestionService> _logger;
    private IMqttClient? _client;

    public bool IsConnected => _client?.IsConnected ?? false;

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new MqttClientFactory();
        _client = factory.CreateMqttClient();

        _client.DisconnectedAsync += async e =>
        {
            if (e.ClientWasConnected && !stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "MQTT disconnected: {Reason}. Reconnecting in {DelayMs}ms...",
                    e.Reason, _mqttOptions.AutoReconnectDelayMs);
                await Task.Delay(_mqttOptions.AutoReconnectDelayMs, stoppingToken);
                try { await ConnectAndSubscribeAsync(stoppingToken); }
                catch (Exception ex) { _logger.LogError(ex, "MQTT reconnect failed"); }
            }
        };

        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;

        await ConnectAndSubscribeAsync(stoppingToken);

        _logger.LogInformation(
            "MQTT ingestion started: {Url} clientId={ClientId}",
            _mqttOptions.WebSocketUrl, _mqttOptions.ClientId);

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("MQTT ingestion stopping...");
        }
        finally
        {
            if (_client is not null)
            {
                await _client.DisconnectAsync();
                _client.Dispose();
            }
        }
    }

    private async Task ConnectAndSubscribeAsync(CancellationToken ct)
    {
        if (_client is null) return;

        var clientOptions = new MqttClientOptionsBuilder()
            .WithWebSocketServer(o => o.WithUri(_mqttOptions.WebSocketUrl))
            .WithClientId(_mqttOptions.ClientId)
            .WithCleanSession(false)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .Build();

        await _client.ConnectAsync(clientOptions, ct);

        await _client.SubscribeAsync(new MqttTopicFilterBuilder()
            .WithTopic("$share/gateway-group/device/+/telemetry")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .Build(), ct);

        await _client.SubscribeAsync(new MqttTopicFilterBuilder()
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
                _logger.LogWarning("MQTT rejected: invalid topic format '{Topic}'", topic);
                _metrics.RecordRejection("mqtt", "invalid_topic");
                return;
            }

            var deviceId = match.Groups["deviceId"].Value;
            var isCritical = match.Groups["rest"].Value == "event/critical";

            var request = JsonSerializer.Deserialize<TelemetryRequest>(
                payload, SerializerSetup.TightOptions);
            if (request is null)
            {
                _logger.LogWarning(
                    "MQTT rejected: deserialization failed device={DeviceId}", deviceId);
                _metrics.RecordRejection("mqtt", "deserialization_failed");
                return;
            }

            var validationResults = new List<ValidationResult>();
            var validationContext = new ValidationContext(request);
            if (!Validator.TryValidateObject(
                    request, validationContext, validationResults, validateAllProperties: true))
            {
                var errors = string.Join("; ", validationResults.Select(r => r.ErrorMessage));
                _logger.LogWarning(
                    "MQTT rejected: validation failed device={DeviceId} errors={Errors}",
                    deviceId, errors);
                _metrics.RecordRejection("mqtt", "validation_failed");
                return;
            }

            var reliability = isCritical ? ReliabilityLevel.Critical : ReliabilityLevel.BestEffort;
            var kafkaTopic = isCritical ? "event.critical" : "telemetry.raw";

            var envelope = TelemetryEnvelopeMapper.ToEnvelope(request, reliability);
            await _producer.ProduceAsync(kafkaTopic, envelope, CancellationToken.None);

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
