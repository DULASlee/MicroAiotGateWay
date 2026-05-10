using System.Text.Json;
using IoTHunter.Shared.Domain;
using IoTHunter.Shared.Infrastructure;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;

namespace DeviceSimulator.Infrastructure;

internal sealed class MqttTelemetrySender : IAsyncDisposable
{
    private readonly ILogger<MqttTelemetrySender> _logger;
    private readonly string _broker;
    private readonly int _port;
    private readonly string? _username;
    private readonly string? _password;
    private IMqttClient? _client;

    public bool IsConnected => _client?.IsConnected ?? false;

    public MqttTelemetrySender(string broker, int port, string? username, string? password, ILogger<MqttTelemetrySender> logger)
    {
        _broker = broker;
        _port = port;
        _username = username;
        _password = password;
        _logger = logger;
    }

    public async Task EnsureConnectedAsync(string clientIdPrefix, CancellationToken ct)
    {
        if (_client?.IsConnected == true) return;

        var factory = new MqttClientFactory();
        _client = factory.CreateMqttClient();
        var clientId = $"{clientIdPrefix}-{Guid.NewGuid():N}";

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(_broker, _port)
            .WithClientId(clientId)
            .WithCleanSession(false);

        if (!string.IsNullOrWhiteSpace(_username))
        {
            optionsBuilder.WithCredentials(_username, _password);
        }

        await _client.ConnectAsync(optionsBuilder.Build(), ct);
        _logger.LogInformation("MQTT Simulator connected as {ClientId}", clientId);
    }

    public async Task<SendResult> SendAsync(TelemetryEnvelope envelope, CancellationToken ct)
    {
        if (_client is null || !_client.IsConnected) return SendResult.FatalFailure;

        var isCritical = envelope.ReliabilityLevel == ReliabilityLevel.Critical;
        var topic = isCritical
            ? $"device/{envelope.DeviceId}/event/critical"
            : $"device/{envelope.DeviceId}/telemetry";

        var payload = JsonSerializer.Serialize(new
        {
            deviceId = envelope.DeviceId,
            metricType = envelope.MetricType,
            payload = JsonSerializer.Deserialize<JsonElement>(envelope.PayloadJson),
            timestamp = envelope.RecordedAt.ToUnixTimeMilliseconds(),
            sequence = envelope.Sequence
        }, SerializerSetup.TightOptions);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(isCritical ? MqttQualityOfServiceLevel.AtLeastOnce : MqttQualityOfServiceLevel.AtMostOnce)
            .Build();

        try
        {
            await _client.PublishAsync(message, ct);
            return SendResult.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQTT publish failed {EventId}", envelope.EventId);
            return SendResult.RetryableFailure;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            if (_client.IsConnected) await _client.DisconnectAsync();
            _client.Dispose();
        }
    }
}
