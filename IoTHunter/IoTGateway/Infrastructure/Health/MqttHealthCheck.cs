using IoTGateway.Infrastructure.Mqtt;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IoTGateway.Infrastructure.Health;

internal sealed class MqttHealthCheck : IHealthCheck
{
    private readonly MqttIngestionService _mqtt;

    public MqttHealthCheck(MqttIngestionService mqtt)
    {
        _mqtt = mqtt;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_mqtt.IsConnected)
            return Task.FromResult(HealthCheckResult.Healthy("MQTT connected"));

        return Task.FromResult(HealthCheckResult.Unhealthy("MQTT disconnected"));
    }
}
