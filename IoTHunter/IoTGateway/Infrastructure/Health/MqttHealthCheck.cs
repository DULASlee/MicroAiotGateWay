using IoTGateway.Infrastructure.Mqtt;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IoTGateway.Infrastructure.Health;

internal sealed class MqttHealthCheck : IHealthCheck
{
    private readonly MqttIngestionService _mqttService;

    public MqttHealthCheck(MqttIngestionService mqttService)
    {
        _mqttService = mqttService;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        if (_mqttService.IsConnected)
        {
            return Task.FromResult(HealthCheckResult.Healthy("MQTT broker connected"));
        }

        return Task.FromResult(HealthCheckResult.Unhealthy("MQTT broker disconnected"));
    }
}
