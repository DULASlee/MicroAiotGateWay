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
    private IMqttClient? _client;

    public bool IsConnected => _client?.IsConnected ?? false;

    public MqttTelemetrySender(ILogger<MqttTelemetrySender> logger)
    {
        _logger = logger;
    }

    public async Task EnsureConnectedAsync(string broker, int port, string clientIdPrefix, CancellationToken ct)
    {
        if (_client?.IsConnected == true) return;

        var factory = new MqttClientFactory();
        _client = factory.CreateMqttClient();

        // ADR-016: 普通压测使用随机 ClientId
        var clientId = $"{clientIdPrefix}-{Guid.NewGuid():N}";

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(broker, port)
            .WithClientId(clientId)
            .WithCleanSession(false)       // ADR-015: 持久会话
            .Build();

        await _client.ConnectAsync(options, ct);
        _logger.LogInformation("MQTT Simulator connected as {ClientId}", clientId);
    }

    public async Task<SendResult> SendAsync(TelemetryEnvelope envelope, CancellationToken ct)
    {
        if (_client is null || !_client.IsConnected)
            return SendResult.FatalFailure;

        var isCritical = envelope.ReliabilityLevel == ReliabilityLevel.Critical;
        var topic = isCritical
            ? $"device/{envelope.DeviceId}/event/critical"
            : $"device/{envelope.DeviceId}/telemetry";

        // 终审修复#2: Payload 严格对齐网关 TelemetryRequest 结构
        var payload = JsonSerializer.Serialize(new
        {
            deviceId = envelope.DeviceId,
            metricType = envelope.MetricType,
            payload = envelope.PayloadJson,
            timestamp = envelope.RecordedAt.ToUnixTimeMilliseconds(),
            sequence = envelope.Sequence
        }, SerializerSetup.TightOptions);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            // ADR-014: 关键事件 QoS 1，普通遥测 QoS 0
            .WithQualityOfServiceLevel(isCritical
                ? MqttQualityOfServiceLevel.AtLeastOnce
                : MqttQualityOfServiceLevel.AtMostOnce)
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
            if (_client.IsConnected)
                await _client.DisconnectAsync();
            _client.Dispose();
        }
    }
}
