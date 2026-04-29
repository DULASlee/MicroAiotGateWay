using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace BackendProcessor.Infrastructure.Health;

internal sealed class PostgresHealthCheck : IHealthCheck
{
    private readonly NpgsqlDataSource _pg;

    public PostgresHealthCheck(NpgsqlDataSource pg) => _pg = pg;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct)
    {
        try
        {
            await using var conn = _pg.CreateConnection();
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL unavailable", ex);
        }
    }
}
