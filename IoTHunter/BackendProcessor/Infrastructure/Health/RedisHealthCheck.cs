using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace BackendProcessor.Infrastructure.Health;

internal sealed class RedisHealthCheck : IHealthCheck
{
    private readonly ConnectionMultiplexer _redis;

    public RedisHealthCheck(ConnectionMultiplexer redis) => _redis = redis;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis unavailable", ex);
        }
    }
}
