using Microsoft.AspNetCore.Mvc;
using Npgsql;
using StackExchange.Redis;

namespace BackendProcessor.Controllers;

[ApiController]
[Route("api/v2/devices")]
public sealed class QueriesController : ControllerBase
{
    private readonly ConnectionMultiplexer _redis;
    private readonly NpgsqlDataSource _pg;

    public QueriesController(ConnectionMultiplexer redis, NpgsqlDataSource pg)
    {
        _redis = redis;
        _pg = pg;
    }

    [HttpGet("{deviceId}/latest")]
    public async Task<IActionResult> GetLatest(string deviceId)
    {
        var db = _redis.GetDatabase();
        var key = new RedisKey($"device:latest:{deviceId}");

        var entries = await db.HashGetAllAsync(key);
        if (entries.Length == 0)
            return NotFound(new { error = "not_found", detail = $"No latest projection for device '{deviceId}'" });

        Response.Headers.Append("X-Data-Freshness", "cache_hit");
        return Ok(entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString()));
    }

    [HttpGet("{deviceId}/history")]
    public async Task<IActionResult> GetHistory(
        string deviceId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? sort = "desc")
    {
        pageSize = Math.Min(pageSize, 200);
        var offset = (Math.Max(page, 1) - 1) * pageSize;
        var order = sort?.ToLowerInvariant() == "asc" ? "ASC" : "DESC";

        await using var conn = _pg.CreateConnection();
        await conn.OpenAsync();

        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM telemetry_records WHERE device_id = $1";
        countCmd.Parameters.AddWithValue(deviceId);
        var totalCount = (long)(await countCmd.ExecuteScalarAsync())!;

        await using var dataCmd = conn.CreateCommand();
        dataCmd.CommandText = $"""
            SELECT event_id, device_id, sequence, metric_type, payload_json,
                   recorded_at, received_at, schema_version, reliability
            FROM telemetry_records
            WHERE device_id = $1
            ORDER BY recorded_at {order}
            LIMIT $2 OFFSET $3
            """;
        dataCmd.Parameters.AddWithValue(deviceId);
        dataCmd.Parameters.AddWithValue(pageSize);
        dataCmd.Parameters.AddWithValue(offset);

        var results = new List<Dictionary<string, object?>>();
        await using var reader = await dataCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.GetValue(i);
            results.Add(row);
        }

        return Ok(new
        {
            page,
            pageSize,
            totalCount,
            hasNextPage = offset + pageSize < totalCount,
            data = results
        });
    }

    [HttpGet("{deviceId}/timeseries")]
    public IActionResult GetTimeseries(string deviceId)
    {
        return StatusCode(501, new
        {
            error = "not_implemented",
            detail = "Timeseries endpoint requires TimescaleDB extension. Set TimeseriesProjection:Enabled=true once configured."
        });
    }
}
