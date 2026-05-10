using IoTGateway.Infrastructure.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IoTGateway.Infrastructure.Health;

internal sealed class KafkaHealthCheck : IHealthCheck
{
    private readonly KafkaProducerService _producer;

    public KafkaHealthCheck(KafkaProducerService producer)
    {
        _producer = producer;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _producer.Ping();
            return Task.FromResult(HealthCheckResult.Healthy("Kafka reachable"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Kafka ping failed", ex));
        }
    }
}
