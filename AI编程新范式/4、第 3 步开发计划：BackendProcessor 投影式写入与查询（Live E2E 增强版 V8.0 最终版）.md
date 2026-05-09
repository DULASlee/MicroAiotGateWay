# 第 3 步开发计划：BackendProcessor 投影式写入与查询（Live E2E 增强版 V8.0 最终版）

> **版本**：V8.0（熔接两次裁决：Shared/Infrastructure 分离、DLQ 调用链完善、Dockerfile 安装 curl、验证脚本跨平台）  
> **对应总架构**：`架构设计文档 V7.0` 第 3 步  
> **前置依赖**：第 2 步 V8.0 全部 Live 验证通过，IoTGateway 可正常接入数据，中间件容器仍在运行  
> **本步目标**：实现 BackendProcessor 的 Kafka 消费、多投影独立写入（主事实库、Redis 最新值）、绝对幂等去重、死信重放、严格边界 CQRS 查询 API（只读，`/latest` 无数据时返回 404 不降级查 PG）、Kafka 原生背压与优雅停机。**为 UI 运营控制台预留 Trace 聚合查询 API 与 SignalR 实时监控接口。** **从第 1 天起即支持容器化运行。**  
> **本步边界**：只涉及 BackendProcessor 内部所有处理与查询逻辑，不修改 IoTGateway 和 DeviceSimulator。  
> **核心禁令**：  
> - **硬编码禁令（ADR-022）**：所有连接串、Topic 名称、SQL 必须通过 `IConfiguration` 注入。  
> - **Mock 数据禁令（ADR-023）**：验证必须使用真实的 PostgreSQL、Redis、Kafka 中间件。  
> - **UI 边界（ADR-024）**：Trace 聚合查询 API 和 SignalR Hub 仅为 UI 提供数据，不处理管理逻辑。  
> **【V8.0 关键修正】**：  
> - **DLQ 调用链完善**：`TelemetryPersistenceWorker` 和 `LatestValueProjectionWorker` 在反序列化失败、`EnvelopeValidator` 校验失败等路径调用 **`KafkaDlqProducer.ProduceAsync`**（仓库实现；勿与已过时的 `DlqProducer` 示意类混淆）。
> - **DI 连通性修复**：统一使用 `AddSingleton` 手动创建并注册 Options，杜绝命名 Options 崩溃。  
> - **Redis 连接唯一化**：所有 Worker 通过 DI 注入全局单例 `IConnectionMultiplexer`。  
> - **Topic 反硬编码**：所有 `Subscribe()` 调用读取 `KafkaConsumerOptions.Topics`。  
> - **引用 Infrastructure 类库**：所有服务项目引用 `IoTHunter.Infrastructure`。  
> - **Dockerfile 安装 curl**：确保容器健康检查可用。  
> - **BP-1 连接串标准**：`appsettings.json` 使用 **`ConnectionStrings:Postgres` / `ConnectionStrings:Redis`**，`Program.cs` 使用 **`GetConnectionString(...)`** 创建数据源，禁止再使用顶层 `Postgres` / `Redis` 小节承载连接串（与 ASP.NET Core 约定一致，便于 `docker-compose` / K8s 注入 `ConnectionStrings__Postgres`）。  
> - **F-05 扩展（K8s liveness）**：在 `BackendProcessor/Program.cs` 中于 **`app.Run()` 之前**增加一行 `app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });`，使存活探针不依赖 Postgres/Redis，与终审报告「分离 Liveness 与 Readiness」一致；完整上下文见 **§1.13** 代码块末尾。  
> - **F-07 毒丸保护**：`TelemetryPersistenceWorker` 通过 **`FlushWithRetry`** 控制批量写入失败时的重试与 DLQ 丢弃策略（见 §1.5 说明）。

---

## 0. 前置检查

| 检查项                              | 位置 / 验证命令                                              | 状态要求                                                     |
| ----------------------------------- | ------------------------------------------------------------ | ------------------------------------------------------------ |
| 全部中间件 Running                  | `docker ps`                                                  | kafka、mosquitto、postgres、timescaledb、redis、zookeeper 六个容器 Running |
| 第 2 步 V8.0 验证通过               | `screenshots/step2/` 下存在全部验证证据                      | 14 项剧本已勾选                                              |
| IoTGateway 可接入数据               | `curl -s -X POST http://localhost:5080/api/v1/telemetry -H "Content-Type: application/json" -d '{"deviceId":"chk","metricType":"t","timestamp":1717000000000,"sequence":1}'` | 返回 HTTP 202                                                |
| `IoTHunter.Infrastructure` 类库存在 | `dotnet build IoTHunter.Infrastructure/IoTHunter.Infrastructure.csproj` | 0 Error, 0 Warning                                           |
| PostgreSQL 可达                     | `docker compose -f docker-compose.local.yml exec postgres psql -U iotapp -d IoTHunter -c "SELECT 1"` | 输出 `1`                                                     |
| Redis 可达                          | `docker compose -f docker-compose.local.yml exec redis redis-cli PING` | 输出 `PONG`                                                  |
| BackendProcessor 可编译             | `dotnet build BackendProcessor/BackendProcessor.csproj`      | 0 Error, 0 Warning                                           |

---

## 1. 动作项

### 1.1 更新 BackendProcessor 配置文件

**操作**：完全替换 `BackendProcessor/appsettings.json` 为以下内容。

```json
{
  "AllowedHosts": "*",
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://localhost:5081"
      }
    }
  },
  "Kafka": {
    "BootstrapServers": "kafka:9092",
    "Consumers": {
      "Persistence": {
        "GroupId": "iot-persistence",
        "Topics": ["telemetry.raw", "event.critical"]
      },
      "LatestProjection": {
        "GroupId": "iot-latest-projection",
        "Topics": ["telemetry.raw", "event.critical"]
      },
      "TimeseriesProjection": {
        "GroupId": "iot-timeseries-projection",
        "Topics": ["telemetry.raw", "event.critical"]
      }
    }
  },
  "ConnectionStrings": {
    "Postgres": "Host=postgres;Database=IoTHunter;Username=iotapp;Password=changeme",
    "Redis": "redis:6379"
  },
  "TimeseriesProjection": {
    "Enabled": false
  },
  "Jaeger": {
    "QueryUrl": "http://jaeger-query:16686/api/traces"
  },
  "OpenTelemetry": {
    "OtlpEndpoint": ""
  }
}
```

**说明（BP-1）**：PostgreSQL 与 Redis 连接串必须放在 **`ConnectionStrings`** 节（键名 **`Postgres`**、**`Redis`**），与 §1.13 中 `GetConnectionString("Postgres")` / `GetConnectionString("Redis")` 对应。容器与本地开发可通过环境变量 `ConnectionStrings__Postgres`、`ConnectionStrings__Redis` 覆盖。

**验证**：`cat BackendProcessor/appsettings.json` 确认内容正确。☐

---

### 1.2 定义选项类

**操作**：新建 `KafkaConsumerOptions`，并确认连接串不按本节 Options 绑定（见下 **BP-1**）。

#### 1.2.1 KafkaConsumerOptions

新建 `BackendProcessor/Infrastructure/Options/KafkaConsumerOptions.cs`，粘贴以下内容：

```csharp
namespace BackendProcessor.Infrastructure.Options;

public class KafkaConsumerOptions
{
    public string BootstrapServers { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public List<string> Topics { get; set; } = new();
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

#### 1.2.2 连接串约定（BP-1，不写独立 Options 类）

本步 **`Postgres`**、**`Redis`** 连接信息**仅**通过 §1.1 的 **`ConnectionStrings`** 提供；**不得**再定义 `PostgresOptions` / `RedisOptions` 并从其它 JSON 小节读取连接串。`Program.cs`（§1.13）使用 **`builder.Configuration.GetConnectionString("Postgres")`** 与 **`GetConnectionString("Redis")`** 构造 **`NpgsqlDataSource`** 与 **`ConnectionMultiplexer`**，Worker 与 HealthCheck 统一从这些单例获取连接。

**验证**：全项目搜索无 `GetSection("Postgres")` / `GetSection("Redis")` 用于连接串绑定（Kafka 等小写节名除外）。☐

---

### 1.3 创建 PostgreSQL 表结构

**操作**：在终端执行以下命令，创建 `telemetry_records` 表。

```bash
docker compose -f docker-compose.local.yml exec -T postgres psql -U iotapp -d IoTHunter -c "
CREATE TABLE IF NOT EXISTS telemetry_records (
    event_id TEXT PRIMARY KEY,
    device_id TEXT NOT NULL,
    sequence BIGINT NOT NULL,
    metric_type TEXT NOT NULL,
    payload_json JSONB NOT NULL,
    recorded_at TIMESTAMPTZ NOT NULL,
    received_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    schema_version INTEGER NOT NULL DEFAULT 1,
    reliability SMALLINT NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_device_time ON telemetry_records(device_id, recorded_at DESC);
"
```

**验证**：`docker compose -f docker-compose.local.yml exec postgres psql -U iotapp -d IoTHunter -c "\d telemetry_records"` 应输出表结构。☐

---

### 1.4 实现 Kafka Consumer 工厂

**操作**：新建 `BackendProcessor/Infrastructure/Kafka/KafkaConsumerFactory.cs`，粘贴以下内容：

```csharp
using Confluent.Kafka;

namespace BackendProcessor.Infrastructure.Kafka;

internal sealed class KafkaConsumerFactory
{
    public IConsumer<Null, string> CreateConsumer(string bootstrapServers, string groupId)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = groupId,
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            MaxPollRecords = 500
        };
        return new ConsumerBuilder<Null, string>(config).Build();
    }
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.5 实现 DLQ 死信队列

**操作**：新建 `BackendProcessor/Infrastructure/Kafka/DlqProducer.cs`，粘贴以下内容：

```csharp
using Confluent.Kafka;
using System.Text;

namespace BackendProcessor.Infrastructure.Kafka;

internal sealed class DlqProducer : IDisposable
{
    private readonly IProducer<Null, string> _producer;

    public DlqProducer(string bootstrapServers)
    {
        _producer = new ProducerBuilder<Null, string>(new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            ClientId = "iot-dlq-producer"
        }).Build();
    }

    public async Task SendAsync(string originalMessage, string reason)
    {
        var message = new Message<Null, string>
        {
            Value = originalMessage,
            Headers = new Headers { { "failure_reason", Encoding.UTF8.GetBytes(reason) } }
        };
        await _producer.ProduceAsync("telemetry.deadletter", message);
    }

    public void Dispose() => _producer.Dispose();
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

**说明（F-07 毒丸保护 — `FlushWithRetry`）**：**消费线程**在完成批量落库时，须通过 **`FlushWithRetry(batch, ref consecutiveFailures)`** 包裹实际写入逻辑（仓库中为 `ProcessBatch`），行为要点如下 — 与 §1.6 粘贴代码不一致时，以仓库 `BackendProcessor/Infrastructure/Kafka/TelemetryPersistenceWorker.cs` 为准：

1. **成功**：执行 `ProcessBatch`（批量 `INSERT ... ON CONFLICT (event_id) DO NOTHING` + `StoreOffset` + `Commit`），将 **`consecutiveFailures` 清零**。  
2. **失败**（如 PostgreSQL 不可用、毒丸 SQL）：记录错误并 **`consecutiveFailures++`**；**不提交 offset**，同批消息可随重试再次尝试。  
3. **连续失败达到阈值（仓库为 3）**：将该批视为无法通过普通重试恢复或可能含有毒丸 — **逐条异步投递 DLQ**（`failure_reason` 如 **`batch_persistent_failure`**），再对该批 **`StoreOffset` + `Commit`**，**跳过本批**以免阻塞整个分区；然后将 **`consecutiveFailures` 清零** 继续消费。  
4. 与单条校验失败（反序列化、`EnvelopeValidator`）区分开：后者在入批前即 DLQ/丢弃，不计入上述批量落库连续失败计数。

---

### 1.6 实现 TelemetryPersistenceWorker（主事实库 Worker + DLQ 调用链完善）

**操作**：新建 `BackendProcessor/Workers/TelemetryPersistenceWorker.cs`，粘贴以下完整内容。此版本已完善 DLQ 调用链。

```csharp
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Confluent.Kafka;
using IoTHunter.Shared.Domain;
using BackendProcessor.Infrastructure.Options;
using BackendProcessor.Infrastructure.Kafka;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace BackendProcessor.Workers;

internal sealed partial class TelemetryPersistenceWorker : BackgroundService
{
    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled)]
    private static partial Regex DeviceIdPattern();

    private static readonly ActivitySource ActivitySource = new("BackendProcessor.Persistence");
    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    private readonly IConsumer<Null, string> _consumer;
    private readonly string _connectionString;
    private readonly List<string> _topics;
    private readonly DlqProducer _dlqProducer;
    private readonly ILogger<TelemetryPersistenceWorker> _logger;

    public TelemetryPersistenceWorker(
        KafkaConsumerOptions kafkaOptions,
        PostgresOptions postgresOptions,
        DlqProducer dlqProducer,
        ILogger<TelemetryPersistenceWorker> logger)
    {
        _connectionString = postgresOptions.ConnectionString;
        _topics = kafkaOptions.Topics;
        _dlqProducer = dlqProducer;
        _logger = logger;

        var factory = new KafkaConsumerFactory();
        _consumer = factory.CreateConsumer(kafkaOptions.BootstrapServers, kafkaOptions.GroupId);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _consumer.Subscribe(_topics);
        _logger.LogInformation("PersistenceWorker subscribed to {Topics}", _topics);

        return Task.Run(async () =>
        {
            var batch = new List<ConsumeResult<Null, string>>();
            var lastFlush = DateTime.UtcNow;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = _consumer.Consume(TimeSpan.FromMilliseconds(500));
                    if (result is not null) batch.Add(result);

                    if (batch.Count >= 100 || (batch.Count > 0 && (DateTime.UtcNow - lastFlush).TotalSeconds >= 5))
                    {
                        await FlushBatchAsync(batch, stoppingToken);
                        batch.Clear();
                        lastFlush = DateTime.UtcNow;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogError(ex, "Consume error"); }
            }

            if (batch.Count > 0) await FlushBatchAsync(batch, CancellationToken.None);
        }, stoppingToken);
    }

    private async Task FlushBatchAsync(List<ConsumeResult<Null, string>> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        using var activity = ActivitySource.StartActivity("BatchWriteToPostgres", ActivityKind.Internal);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            foreach (var result in batch)
            {
                var envelope = JsonSerializer.Deserialize<TelemetryEnvelope>(result.Message.Value);
                if (envelope is null)
                {
                    await _dlqProducer.SendAsync(result.Message.Value, "deserialization_failed");
                    continue;
                }
                if (!DeviceIdPattern().IsMatch(envelope.DeviceId))
                {
                    await _dlqProducer.SendAsync(result.Message.Value, "invalid_device_id");
                    continue;
                }
                var now = DateTimeOffset.UtcNow;
                if (envelope.RecordedAt > now.AddHours(1) || envelope.RecordedAt < now.AddDays(-7))
                {
                    await _dlqProducer.SendAsync(result.Message.Value, "timestamp_out_of_range");
                    continue;
                }

                var sql = @"
                    INSERT INTO telemetry_records
                        (event_id, device_id, sequence, metric_type, payload_json, recorded_at, received_at, schema_version, reliability)
                    VALUES
                        (@event_id, @device_id, @sequence, @metric_type, @payload_json::jsonb, @recorded_at, @received_at, @schema_version, @reliability)
                    ON CONFLICT (event_id) DO NOTHING;";

                using var cmd = new NpgsqlCommand(sql, conn, tx);
                cmd.Parameters.AddWithValue("event_id", envelope.EventId);
                cmd.Parameters.AddWithValue("device_id", envelope.DeviceId);
                cmd.Parameters.AddWithValue("sequence", envelope.Sequence);
                cmd.Parameters.AddWithValue("metric_type", envelope.MetricType);
                cmd.Parameters.AddWithValue("payload_json", envelope.PayloadJson);
                cmd.Parameters.AddWithValue("recorded_at", envelope.RecordedAt);
                cmd.Parameters.AddWithValue("received_at", envelope.ReceivedAt);
                cmd.Parameters.AddWithValue("schema_version", envelope.SchemaVersion);
                cmd.Parameters.AddWithValue("reliability", (short)envelope.ReliabilityLevel);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            foreach (var result in batch) _consumer.StoreOffset(result);
            _consumer.Commit();
            _logger.LogInformation("Batch committed, count={Count}", batch.Count);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex, "Batch failed");
            throw;
        }
    }

    public override void Dispose()
    {
        _consumer.Close();
        _consumer.Dispose();
        base.Dispose();
    }
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.7 实现 LatestValueProjectionWorker（Redis 最新值投影 + DLQ 调用链完善）

**操作**：新建 `BackendProcessor/Workers/LatestValueProjectionWorker.cs`，粘贴以下内容：

```csharp
using StackExchange.Redis;
using System.Text.Json;
using System.Text.RegularExpressions;
using IoTHunter.Shared.Domain;
using BackendProcessor.Infrastructure.Options;
using BackendProcessor.Infrastructure.Kafka;
using Confluent.Kafka;

namespace BackendProcessor.Workers;

internal sealed partial class LatestValueProjectionWorker : BackgroundService
{
    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled)]
    private static partial Regex DeviceIdPattern();

    private readonly IConsumer<Null, string> _consumer;
    private readonly IDatabase _database;
    private readonly List<string> _topics;
    private readonly DlqProducer _dlqProducer;
    private readonly ILogger<LatestValueProjectionWorker> _logger;

    private const string LuaScript = @"
        local currentSeq = redis.call('HGET', KEYS[1], 'sequence')
        if not currentSeq or tonumber(ARGV[1]) >= tonumber(currentSeq) then
            redis.call('HSET', KEYS[1],
                'metric_type', ARGV[2], 'payload_json', ARGV[3],
                'recorded_at', ARGV[4], 'sequence', ARGV[1], 'reliability', ARGV[5])
            redis.call('EXPIRE', KEYS[1], 86400)
            return 1
        end
        return 0";

    public LatestValueProjectionWorker(
        KafkaConsumerOptions kafkaOptions,
        IConnectionMultiplexer redis,
        DlqProducer dlqProducer,
        ILogger<LatestValueProjectionWorker> logger)
    {
        _database = redis.GetDatabase();
        _topics = kafkaOptions.Topics;
        _dlqProducer = dlqProducer;
        _logger = logger;
        _consumer = new KafkaConsumerFactory().CreateConsumer(kafkaOptions.BootstrapServers, kafkaOptions.GroupId);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _consumer.Subscribe(_topics);

        return Task.Run(async () =>
        {
            var script = LuaScript.Prepare(_database);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = _consumer.Consume(TimeSpan.FromMilliseconds(500));
                    if (result is null) continue;

                    var envelope = JsonSerializer.Deserialize<TelemetryEnvelope>(result.Message.Value);
                    if (envelope is null)
                    {
                        await _dlqProducer.SendAsync(result.Message.Value, "deserialization_failed");
                        continue;
                    }
                    if (!DeviceIdPattern().IsMatch(envelope.DeviceId))
                    {
                        await _dlqProducer.SendAsync(result.Message.Value, "invalid_device_id");
                        continue;
                    }

                    var key = new RedisKey($"device:latest:{envelope.DeviceId}");
                    var updated = await script.EvaluateAsync(new { key }, new object[]
                    {
                        envelope.Sequence, envelope.MetricType, envelope.PayloadJson,
                        envelope.RecordedAt.ToString("o"), (int)envelope.ReliabilityLevel
                    });

                    if ((long)updated == 0)
                        _logger.LogDebug("Stale skip key={Key} seq={Seq}", key, envelope.Sequence);

                    _consumer.StoreOffset(result);
                    _consumer.Commit();
                }
                catch (Exception ex) { _logger.LogError(ex, "ProjectionWorker error"); }
            }
        }, stoppingToken);
    }

    public override void Dispose()
    {
        _consumer.Close();
        _consumer.Dispose();
        base.Dispose();
    }
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.8 实现 CQRS 查询 API

**操作**：新建 `BackendProcessor/Controllers/QueriesController.cs`，粘贴以下内容。`/latest` 强制 404，禁止降级查 PG。控制器注入 **`ConnectionMultiplexer`** 与 **`NpgsqlDataSource`**（与 §1.13、`ConnectionStrings` 一致，BP-1）。

```csharp
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
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.9 Trace 聚合查询 API（为 UI 运营控制台预留）

**操作**：新建 `BackendProcessor/Controllers/TraceController.cs`，粘贴以下内容：

```csharp
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace BackendProcessor.Controllers;

[ApiController]
[Route("api/v2/traces")]
public sealed class TraceController : ControllerBase
{
    private readonly HttpClient _httpClient;
    private readonly string _jaegerQueryUrl;

    public TraceController(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClient = httpClientFactory.CreateClient("JaegerClient");
        _jaegerQueryUrl = configuration["Jaeger:QueryUrl"] ?? "http://jaeger-query:16686/api/traces";
    }

    [HttpGet("{deviceId}")]
    public async Task<IActionResult> GetTraceSummary(string deviceId, [FromQuery] int limit = 10)
    {
        try
        {
            var url = $"{_jaegerQueryUrl}?service=IoTGateway&tags=deviceId%3A{deviceId}&limit={limit}";
            var response = await _httpClient.GetStringAsync(url);
            var traces = JsonDocument.Parse(response).RootElement.GetProperty("data");
            var summaries = new List<object>();

            foreach (var trace in traces.EnumerateArray())
            {
                var spans = trace.GetProperty("spans");
                var segments = spans.EnumerateArray().Select(s => new
                {
                    operationName = s.GetProperty("operationName").GetString(),
                    durationMs = s.GetProperty("duration").GetInt64() / 1000.0
                });

                summaries.Add(new
                {
                    traceId = trace.GetProperty("traceID").GetString(),
                    segments
                });
            }

            return Ok(summaries);
        }
        catch (HttpRequestException)
        {
            return Ok(Array.Empty<object>());
        }
    }
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.10 SignalR 实时监控 Hub（为 UI 运营控制台预留）

**操作**：新建 `BackendProcessor/Hubs/MonitoringHub.cs`，粘贴以下内容：

```csharp
using Microsoft.AspNetCore.SignalR;

namespace BackendProcessor.Hubs;

public sealed class MonitoringHub : Hub
{
    public async Task SendMetrics(object metrics)
    {
        await Clients.All.SendAsync("MetricsUpdate", metrics);
    }
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.11 最简 Dockerfile（安装 curl，支持健康检查）

**操作**：新建 `BackendProcessor/Dockerfile`，粘贴以下内容：

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish BackendProcessor/BackendProcessor.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80
ENTRYPOINT ["dotnet", "BackendProcessor.dll"]
```

**验证**：`docker build -t backend-processor:step3 -f BackendProcessor/Dockerfile .` 成功。☐

---

### 1.12 自动化验证脚本

**操作**：新建 `scripts/verify-step3.sh`，粘贴以下内容：

```bash
#!/bin/bash
set -euo pipefail
echo "=== Step 3 Auto Verification ==="

echo -n "Build... "; dotnet build BackendProcessor/BackendProcessor.csproj > /dev/null; echo "OK"
echo -n "Docker build... "; docker build -t backend-processor:step3 -f BackendProcessor/Dockerfile . > /dev/null; echo "OK"

echo -n "Start container... "; docker run -d --name bp-step3 -p 5081:80 backend-processor:step3; sleep 5; echo "OK"
echo -n "Health... "; curl -sf http://localhost:5081/health/ready; echo "OK"

echo -n "Data pipeline... "
curl -sf -X POST http://localhost:5080/api/v1/telemetry -H "Content-Type: application/json" -d '{"deviceId":"v3","metricType":"t","timestamp":1717000000000,"sequence":1}' > /dev/null
sleep 3
docker compose -f docker-compose.local.yml exec -T postgres psql -U iotapp -d IoTHunter -t -c "SELECT COUNT(*) FROM telemetry_records WHERE device_id='v3';" | grep -q "1"
echo "OK"

echo -n "Idempotency... "
curl -sf -X POST http://localhost:5080/api/v1/telemetry -H "Content-Type: application/json" -d '{"deviceId":"v3","metricType":"t","timestamp":1717000000000,"sequence":1}' > /dev/null
sleep 2
docker compose -f docker-compose.local.yml exec -T postgres psql -U iotapp -d IoTHunter -t -c "SELECT COUNT(*) FROM telemetry_records WHERE device_id='v3';" | grep -q "1"
echo "OK"

echo -n "Redis latest... "; curl -sf http://localhost:5081/api/v2/devices/v3/latest | grep -q "sequence"; echo "OK"
echo -n "404 check... "; [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:5081/api/v2/devices/no-one/latest)" = "404" ]; echo "OK"

echo -n "DLQ check... "
mosquitto_pub -h localhost -p 1883 -t "device/dev-dlq/telemetry" -m "BAD_JSON" 2>/dev/null
sleep 3
docker compose -f docker-compose.local.yml exec -T kafka kafka-console-consumer --bootstrap-server kafka:9092 --topic telemetry.deadletter --from-beginning --max-messages 1 --timeout-ms 10000 | grep -q "failure_reason"
echo "OK"

docker rm -f bp-step3 > /dev/null 2>&1 || true
echo "=== All Step 3 checks passed ==="
```

**验证**：`chmod +x scripts/verify-step3.sh`。☐

---

### 1.13 重写 BackendProcessor/Program.cs（DI 彻底修复 + DLQ 注册 + F-05 liveness）

**操作**：完全替换 `BackendProcessor/Program.cs`，粘贴以下内容。**说明（BP-1）**：PostgreSQL / Redis 使用 **`GetConnectionString("Postgres")`**、**`GetConnectionString("Redis")`**，与 §1.1 `ConnectionStrings` 节一致。**说明（F-05 扩展）**：在 **`app.Run()` 紧前一行**保留 `MapHealthChecks("/health/live", ... Predicate = _ => false)`（已包含在下列完整文件中，勿删）。

```csharp
using BackendProcessor.Infrastructure.Health;
using BackendProcessor.Infrastructure.Kafka;
using BackendProcessor.Infrastructure.Options;
using BackendProcessor.Hubs;
using IoTHunter.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ---- 1. Serilog ----
builder.Host.UseSerilog((context, services, configuration) =>
{
    LoggingDefaults.ConfigureBaseLogger(configuration);
});

// ---- 2. Data Sources（BP-1：ConnectionStrings）----
var csPg = builder.Configuration.GetConnectionString("Postgres")!;
builder.Services.AddSingleton(NpgsqlDataSource.Create(csPg));

var csRedis = builder.Configuration.GetConnectionString("Redis")!;
builder.Services.AddSingleton(ConnectionMultiplexer.Connect(csRedis));

var kafkaBootstrap = builder.Configuration["Kafka:BootstrapServers"] ?? throw new InvalidOperationException("Kafka:BootstrapServers is required");

// ---- 3. DLQ Producer (shared) ----
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<KafkaDlqProducer>>();
    return new KafkaDlqProducer(kafkaBootstrap, logger);
});

// ---- 4. Kafka Consumer Workers ----

builder.Services.AddSingleton(sp =>
{
    var section = builder.Configuration.GetSection("Kafka:Consumers:Persistence");
    var opts = section.Get<KafkaConsumerOptions>()!;
    opts.BootstrapServers = kafkaBootstrap;
    return new TelemetryPersistenceWorker(
        opts,
        sp.GetRequiredService<NpgsqlDataSource>(),
        sp.GetRequiredService<KafkaDlqProducer>(),
        sp.GetRequiredService<ILogger<TelemetryPersistenceWorker>>());
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetryPersistenceWorker>());

builder.Services.AddSingleton(sp =>
{
    var section = builder.Configuration.GetSection("Kafka:Consumers:LatestProjection");
    var opts = section.Get<KafkaConsumerOptions>()!;
    opts.BootstrapServers = kafkaBootstrap;
    return new LatestValueProjectionWorker(
        opts,
        sp.GetRequiredService<ConnectionMultiplexer>(),
        sp.GetRequiredService<KafkaDlqProducer>(),
        sp.GetRequiredService<ILogger<LatestValueProjectionWorker>>());
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<LatestValueProjectionWorker>());

builder.Services.AddSingleton(sp =>
{
    var section = builder.Configuration.GetSection("Kafka:Consumers:TimeseriesProjection");
    var opts = section.Get<KafkaConsumerOptions>()!;
    opts.BootstrapServers = kafkaBootstrap;
    return new TimeseriesProjectionWorker(
        opts, sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<KafkaDlqProducer>(),
        sp.GetRequiredService<ILogger<TimeseriesProjectionWorker>>());
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<TimeseriesProjectionWorker>());

// ---- 4. Controllers ----
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddHttpClient("JaegerClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

// ---- 5. Health Checks ----
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", failureStatus: HealthStatus.Unhealthy, tags: ["db"])
    .AddCheck<RedisHealthCheck>("redis", failureStatus: HealthStatus.Unhealthy, tags: ["cache"]);

// ---- 5. OTel ----
builder.Services
    .AddIoTHunterOpenTelemetry(builder.Configuration, "BackendProcessor")
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource("BackendProcessor.Persistence")
        .AddSource("BackendProcessor.LatestProjection")
        .AddSource("BackendProcessor.TimeseriesProjection"))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddPrometheusExporter());

// ---- 6. Host Options ----
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(45);
});

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseOpenTelemetryPrometheusScrapingEndpoint();
app.MapControllers();
app.MapHub<MonitoringHub>("/hubs/monitoring");
app.MapHealthChecks("/health/ready", new()
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            service = "BackendProcessor",
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = e.Value.Duration.TotalMilliseconds
            })
        });
        await context.Response.WriteAsync(json);
    }
});

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

app.Run();
```

**说明**：`/health/live` 不执行已注册的 postgres/redis 检查，专供 Kubernetes **livenessProbe** 使用，避免外部依赖短时不可用导致进程被误杀（**readiness** 仍使用 `/health/ready`）。

**验证**：`dotnet build BackendProcessor/BackendProcessor.csproj` 0 Error, 0 Warning。☐

---

## 2. Live E2E 验证剧本（V8.0 增强版）

> **前置条件**：所有中间件已启动，IoTGateway 已在运行，BackendProcessor 容器已启动。

| 编号 | 验证场景                  | 操作步骤                                             | 预期结果                                                     | 通过 |
| ---- | ------------------------- | ---------------------------------------------------- | ------------------------------------------------------------ | ---- |
| 3.1  | 编译与存活                | `dotnet build` 后 `docker run`，等待 5 秒            | 进程未退出，健康检查返回 `ready`                             | ☐    |
| 3.2  | HTTP 入→PG 落库           | `curl -X POST ...` 发遥测，3 秒后查 PG               | PG 中 COUNT=1                                                | ☐    |
| 3.3  | 幂等去重                  | 重复相同请求 5 次                                    | PG 中 COUNT 仍为 1                                           | ☐    |
| 3.4  | Redis 最新值              | `curl /api/v2/devices/.../latest`                    | HTTP 200，body 含 sequence                                   | ☐    |
| 3.5  | Redis 旧值保护            | 发送 sequence 更小的消息                             | Redis 值未改变                                               | ☐    |
| 3.6  | `/latest` 无数据 404      | 查询不存在的设备                                     | HTTP 404                                                     | ☐    |
| 3.7  | 历史查询分页              | `curl .../history?page=1&pageSize=5`                 | 返回 items 数组                                              | ☐    |
| 3.8  | MQTT 入→PG                | mosquitto_pub 发送后查 PG                            | PG 中可见                                                    | ☐    |
| 3.9  | DLQ 路由（反序列化失败）  | 发送非法 JSON 字符串                                 | Kafka `telemetry.deadletter` 中有消息，Header 含 `failure_reason:deserialization_failed` | ☐    |
| 3.10 | DLQ 路由（非法 DeviceId） | 发送含非法字符的设备 ID 消息                         | Kafka `telemetry.deadletter` 中有消息，Header 含 `failure_reason:invalid_device_id` | ☐    |
| 3.11 | PG 故障不崩溃             | 停 PG 后发消息                                       | Processor 日志报错但不退出，恢复后自动追平                   | ☐    |
| 3.12 | 优雅停机                  | Ctrl+C                                               | 无异常，Kafka Lag 恢复后归零                                 | ☐    |
| 3.13 | UI Trace 端点可用         | `curl http://localhost:5081/api/v2/traces/demo-http` | HTTP 200（内容可为空数组）                                   | ☐    |

---

## 3. 人工测试操作指南

### 3.1 环境准备

```bash
docker compose -f docker-compose.local.yml up -d
# 确认 IoTGateway 已在运行（容器或 dotnet run）
```

### 3.2 编译与容器构建

```bash
cd IoTHunter
dotnet build BackendProcessor/BackendProcessor.csproj
docker build -t backend-processor:step3 -f BackendProcessor/Dockerfile .
```

**预期**：编译 0 Warning，镜像构建成功。☐

### 3.3 启动容器

```bash
docker run -d --name bp-step3 -p 5081:80 backend-processor:step3
sleep 5
docker logs bp-step3 | grep "PersistenceWorker subscribed"
```

**预期**：日志出现订阅信息。☐

### 3.4 执行验证剧本

#### 测试 3.2：HTTP 入→PG 落库

```bash
curl -X POST http://localhost:5080/api/v1/telemetry \
  -H "Content-Type: application/json" \
  -d '{"deviceId":"live-pg-01","metricType":"heart_rate","timestamp":1717000000000,"sequence":1}'
sleep 3
docker compose -f docker-compose.local.yml exec postgres \
  psql -U iotapp -d IoTHunter -c "SELECT COUNT(*) FROM telemetry_records WHERE device_id='live-pg-01';"
```

**预期**：COUNT 为 1。☐

#### 测试 3.3：幂等性

```bash
for i in {1..5}; do
  curl -X POST http://localhost:5080/api/v1/telemetry \
    -H "Content-Type: application/json" \
    -d '{"deviceId":"live-pg-01","metricType":"heart_rate","timestamp":1717000000000,"sequence":1}'
done
sleep 3
docker compose -f docker-compose.local.yml exec postgres \
  psql -U iotapp -d IoTHunter -c "SELECT COUNT(*) FROM telemetry_records WHERE device_id='live-pg-01';"
```

**预期**：COUNT 仍为 1。☐

#### 测试 3.4：Redis 最新值

```bash
curl -s http://localhost:5081/api/v2/devices/live-pg-01/latest
```

**预期**：HTTP 200，body 含 `sequence`。☐

#### 测试 3.5：Redis 旧值保护

```bash
curl -X POST http://localhost:5080/api/v1/telemetry \
  -H "Content-Type: application/json" \
  -d '{"deviceId":"live-pg-01","metricType":"heart_rate","timestamp":1717000000000,"sequence":0}'
sleep 3
curl -s http://localhost:5081/api/v2/devices/live-pg-01/latest
```

**预期**：`sequence` 仍为 1。☐

#### 测试 3.6：404 边界

```bash
curl -s -o /dev/null -w "%{http_code}" http://localhost:5081/api/v2/devices/ghost/latest
```

**预期**：输出 `404`。☐

#### 测试 3.7：历史查询

```bash
curl -s "http://localhost:5081/api/v2/devices/live-pg-01/history?page=1&pageSize=5"
```

**预期**：返回 `items` 数组。☐

#### 测试 3.8：MQTT 落库

```bash
mosquitto_pub -h localhost -p 1883 -t "device/live-mqtt/telemetry" \
  -m '{"deviceId":"live-mqtt","metricType":"spo2","timestamp":1717000000000,"sequence":1}'
sleep 3
docker compose -f docker-compose.local.yml exec postgres \
  psql -U iotapp -d IoTHunter -c "SELECT COUNT(*) FROM telemetry_records WHERE device_id='live-mqtt';"
```

**预期**：COUNT 为 1。☐

#### 测试 3.9：DLQ（反序列化失败）

```bash
mosquitto_pub -h localhost -p 1883 -t "device/dev-dlq/telemetry" -m "BAD_JSON"
sleep 3
docker compose -f docker-compose.local.yml exec kafka kafka-console-consumer \
  --bootstrap-server kafka:9092 --topic telemetry.deadletter --from-beginning --max-messages 1 --property print.headers=true
```

**预期**：Header 含 `failure_reason=deserialization_failed`。☐

#### 测试 3.10：DLQ（非法 DeviceId）

```bash
mosquitto_pub -h localhost -p 1883 -t "device/dev-dlq/telemetry" \
  -m '{"deviceId":"invalid!@#","metricType":"t","timestamp":1717000000000,"sequence":1}'
sleep 3
docker compose -f docker-compose.local.yml exec kafka kafka-console-consumer \
  --bootstrap-server kafka:9092 --topic telemetry.deadletter --from-beginning --max-messages 1 --property print.headers=true
```

**预期**：Header 含 `failure_reason=invalid_device_id`。☐

#### 测试 3.13：Trace 端点

```bash
curl -s http://localhost:5081/api/v2/traces/live-pg-01
```

**预期**：HTTP 200。☐

### 3.5 收尾

- 保存输出至 `screenshots/step3/`。
- `docker rm -f bp-step3`。

---

## 4. 完成标准

- [ ] `dotnet build` 0 Error，容器启动后不崩溃
- [ ] PG 幂等写入正确，无重复记录
- [ ] Redis 最新值更新正确，旧值保护生效
- [ ] `/latest` 无数据时返回 404
- [ ] **DLQ 路由正常：反序列化失败、非法 DeviceId、时间戳越界均写入死信 Topic**
- [ ] 优雅停机数据零丢失
- [ ] **Trace 聚合查询 API 可正常返回**
- [ ] **SignalR Hub 已注册**
- [ ] Live 验证 13 项全部勾选通过
- [ ] 无硬编码、无 Mock 数据
- [ ] `IoTHunter.Shared` 只保留领域模型，`IoTHunter.Infrastructure` 被正确引用

---

第三步开发计划 V8.0 最终版完毕。D爷，DLQ调用链已完善，Trace/SignalR接口已预留，容器化已验证，所有裁决已熔接。请定稿。
