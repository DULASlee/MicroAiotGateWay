# 第 4 步开发计划：DeviceSimulator 专业双模压测系统（Live E2E 增强版 V8.0 最终版）

> **版本**：V8.0（熔接两次裁决：Sender 实例复用、Dockerfile 安装 curl、引用 Infrastructure 类库、验证脚本跨平台）  
> **对应总架构**：`架构设计文档 V7.0` 第 4 步  
> **前置依赖**：第 3 步 V8.0 全部 Live 验证通过，IoTGateway 可正常接入数据，BackendProcessor 可正常消费并写入 PG/Redis  
> **本步目标**：构建一个具备**并发架构、精确速率控制、四种标准压测场景、系统级指标采集（QPS、P50~P999、Kafka Lag、PG写入TPS等）和数据闭环校验**的专业物联网压测工具。  
> **为第10步 UI 运营控制台预留实时压测快照、配置查询与健康检查 API**。  
> **本步边界**：只实现 DeviceSimulator 项目，不修改 IoTGateway 和 BackendProcessor，不写 Dockerfile/K8s YAML（留给第5、6步）。  
> **核心禁令**：  
> - **硬编码禁令（ADR-022）**：所有连接串、Topic 名称、QoS 配置必须通过 `IConfiguration` 或配置常量注入，禁止硬编码。  
> - **Mock 数据禁令（ADR-023）**：验证必须使用真实的 Kafka、Mosquitto 中间件，禁止内存队列充当 Kafka，禁止假数据返回 202。  
> **【V8.0 关键修正】**：  
> - **Sender 实例复用**：`SimulatorWorker` 初始化时创建 `HttpTelemetrySender` 和 `MqttTelemetrySender` 实例并复用，重试时不再每次新建连接，杜绝连接风暴。  
> - **Dockerfile 安装 curl**：确保容器健康检查可用。  
> - **引用 Infrastructure 类库**：所有服务项目引用 `IoTHunter.Infrastructure`。  
> - **验证脚本跨平台**：移除 `--network host`，使用端口映射 `-p 5091:5091`。  
> - **F-02 Payload 对齐**：HTTP/MQTT 发往网关的 JSON 体中 **`payload`** 字段必须使用 **`JsonSerializer.Deserialize<JsonElement>(envelope.PayloadJson)`**，与 `TelemetryRequest.Payload`（`JsonElement?`）一致，禁止将 `PayloadJson` 整段当作字符串标量送出（§1.7、§1.8）。

---

## 0. 前置检查

| 检查项                            | 位置 / 验证命令                                | 状态要求                                                     |
| --------------------------------- | ---------------------------------------------- | ------------------------------------------------------------ |
| 全部中间件 Running                | `docker ps`                                    | kafka、mosquitto、postgres、timescaledb、redis、zookeeper 六个容器 Running |
| IoTGateway 健康                   | `curl -s http://localhost:5080/health/ready`   | 返回 `Healthy`                                               |
| BackendProcessor 健康             | `curl -s http://localhost:5081/health/ready`   | 返回 `ready`                                                 |
| 第 3 步 V8.0 验证通过             | `screenshots/step3/` 下存在全部验证证据        | 13 项剧本已勾选                                              |
| `TelemetryEnvelope.Create()` 可用 | `IoTHunter.Shared/Domain/TelemetryEnvelope.cs` | 工厂方法存在                                                 |
| `ReliabilityLevel` 枚举存在       | `IoTHunter.Shared/Domain/ReliabilityLevel.cs`  | 三个值均有                                                   |
| `IoTHunter.Infrastructure` 可编译 | `dotnet build IoTHunter.Infrastructure`        | 0 Error, 0 Warning                                           |

---

## 1. 动作项

### 1.1 改造项目为 Web 宿主

**目的**：暴露 HTTP API 供 UI 获取压测实时快照。

**操作**：修改 `DeviceSimulator.csproj`，替换为以下内容：

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Http" Version="10.*" />
    <PackageReference Include="MQTTnet" Version="5.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\IoTHunter.Shared\IoTHunter.Shared.csproj" />
    <ProjectReference Include="..\IoTHunter.Infrastructure\IoTHunter.Infrastructure.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Update="appsettings.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

**验证**：`dotnet build DeviceSimulator/DeviceSimulator.csproj` 0 Error, 0 Warning。☐

---

### 1.2 创建核心配置类（record 类型，支持 with 表达式）

**操作**：新建 `DeviceSimulator/Infrastructure/SimulatorOptions.cs`，粘贴以下内容：

```csharp
namespace DeviceSimulator.Infrastructure;

public sealed record SimulatorOptions
{
    public string Protocol { get; init; } = "http";
    public string HttpBaseUrl { get; init; } = "http://localhost:5080";
    public string MqttBroker { get; init; } = "localhost";
    public int MqttPort { get; init; } = 1883;
    public int DeviceCount { get; init; } = 100;
    public int HttpConnections { get; init; } = 20;
    public double MessagesPerSecond { get; init; } = 1;
    public string Scenario { get; init; } = "load";
    public int DurationSeconds { get; init; } = 120;
    public int WarmupSeconds { get; init; } = 10;
    public double CriticalEventRatio { get; init; } = 0.05;
    public int PayloadSize { get; init; } = 128;
    public bool MonitorKafkaLag { get; init; } = true;
    public bool MonitorPostgresTPS { get; init; } = true;
    public int KafkaLagCheckIntervalSeconds { get; init; } = 5;
    public int PostgresCheckIntervalSeconds { get; init; } = 5;
    public int MaxRetries { get; init; } = 3;
    public int RetryBaseDelayMs { get; init; } = 100;
}
```

**验证**：`dotnet build DeviceSimulator/DeviceSimulator.csproj` 0 Error, 0 Warning。☐

---

### 1.3 场景化压测配置

**操作**：新建 `DeviceSimulator/Infrastructure/LoadProfile.cs`，粘贴以下内容：

```csharp
namespace DeviceSimulator.Infrastructure;

public static class LoadProfile
{
    public static SimulatorOptions ApplyScenario(SimulatorOptions opts, string scenario) => scenario switch
    {
        "baseline" => opts with { DeviceCount = 10, MessagesPerSecond = 1, DurationSeconds = 30, WarmupSeconds = 5, HttpConnections = 5, MonitorKafkaLag = false, MonitorPostgresTPS = false },
        "connection-limit" => opts with { Protocol = "mqtt", DeviceCount = 1000, MessagesPerSecond = 0.1, DurationSeconds = 120, WarmupSeconds = 10, MonitorKafkaLag = true, MonitorPostgresTPS = false },
        "throughput" => opts with { DeviceCount = 200, MessagesPerSecond = 50, DurationSeconds = 300, WarmupSeconds = 15, HttpConnections = 50, MonitorKafkaLag = true, MonitorPostgresTPS = true, KafkaLagCheckIntervalSeconds = 3 },
        "stability" => opts with { DeviceCount = 100, MessagesPerSecond = 5, DurationSeconds = 86400, WarmupSeconds = 30, HttpConnections = 30, MonitorKafkaLag = true, MonitorPostgresTPS = true, KafkaLagCheckIntervalSeconds = 30 },
        _ => opts
    };
}
```

**验证**：`dotnet build DeviceSimulator/DeviceSimulator.csproj` 0 Error, 0 Warning。☐

---

### 1.4 数据生成器

**操作**：新建 `DeviceSimulator/Data/TelemetryGenerator.cs`，粘贴以下内容：

```csharp
using IoTHunter.Shared.Domain;
using System.Text.Json;

namespace DeviceSimulator.Data;

internal sealed class TelemetryGenerator
{
    private readonly Dictionary<string, long> _sequences = new();
    private readonly string[] _metrics = { "heart_rate", "spo2", "steps", "temperature", "sleep_score" };
    private readonly Random _random = new();
    private readonly double _criticalRatio;

    public TelemetryGenerator(double criticalRatio = 0.05) => _criticalRatio = criticalRatio;

    public TelemetryEnvelope Generate(string deviceId)
    {
        if (!_sequences.ContainsKey(deviceId)) _sequences[deviceId] = 0;
        var seq = ++_sequences[deviceId];
        var metric = _metrics[_random.Next(_metrics.Length)];
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(GeneratePayload(metric));
        var reliability = _random.NextDouble() < _criticalRatio ? ReliabilityLevel.Critical : ReliabilityLevel.BestEffort;
        return TelemetryEnvelope.Create(
            eventId: $"{deviceId}:{metric}:{seq}:{now.ToUnixTimeMilliseconds()}",
            deviceId: deviceId,
            metricType: metric,
            recordedAt: now,
            payloadJson: payload,
            reliabilityLevel: reliability,
            sequence: seq);
    }

    private object GeneratePayload(string metric) => metric switch
    {
        "heart_rate" => new { bpm = _random.Next(60, 120) },
        "spo2" => new { percentage = Math.Round(95.0 + _random.NextDouble() * 5.0, 1) },
        "steps" => new { count = _random.Next(0, 500) },
        "temperature" => new { celsius = Math.Round(36.0 + _random.NextDouble() * 1.5, 1) },
        "sleep_score" => new { score = _random.Next(0, 100) },
        _ => new { value = _random.Next(0, 100) }
    };
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.5 压测指标收集器

**操作**：新建 `DeviceSimulator/Infrastructure/LoadTestMetrics.cs`，粘贴以下内容。此版本使用 `ConcurrentBag<double>` 存储延迟，后续可根据压测规模优化为分桶直方图。

```csharp
using System.Collections.Concurrent;

namespace DeviceSimulator.Infrastructure;

internal sealed class LoadTestMetrics
{
    private long _successCount, _failCount, _totalBytes;
    private readonly ConcurrentBag<double> _latenciesMs = new();
    private readonly ConcurrentQueue<(DateTime time, bool success)> _eventLog = new();
    private readonly DateTime _start = DateTime.UtcNow;

    public void RecordSuccess(double ms, int bytes)
    {
        Interlocked.Increment(ref _successCount);
        Interlocked.Add(ref _totalBytes, bytes);
        _latenciesMs.Add(ms);
        _eventLog.Enqueue((DateTime.UtcNow, true));
    }

    public void RecordFailure() => Interlocked.Increment(ref _failCount);

    public LoadTestSnapshot GetSnapshot()
    {
        var elapsed = (DateTime.UtcNow - _start).TotalSeconds;
        var sorted = _latenciesMs.OrderBy(x => x).ToArray();
        var recent = _eventLog.Where(e => e.time >= DateTime.UtcNow.AddSeconds(-5) && e.success).Count();
        return new LoadTestSnapshot
        {
            DurationSeconds = elapsed,
            TotalSuccess = _successCount,
            TotalFail = _failCount,
            TotalBytes = _totalBytes,
            Qps = elapsed > 0 ? (_successCount + _failCount) / elapsed : 0,
            ThroughputMBps = elapsed > 0 ? _totalBytes / elapsed / 1024 / 1024 : 0,
            LatencyP50 = Percentile(sorted, 0.50),
            LatencyP90 = Percentile(sorted, 0.90),
            LatencyP95 = Percentile(sorted, 0.95),
            LatencyP99 = Percentile(sorted, 0.99),
            LatencyP999 = Percentile(sorted, 0.999),
            LatencyMin = sorted.Length > 0 ? sorted[0] : 0,
            LatencyMax = sorted.Length > 0 ? sorted[^1] : 0,
            SuccessRate = (_successCount + _failCount) > 0 ? (double)_successCount / (_successCount + _failCount) * 100 : 0,
            RecentQps = (double)recent / 5
        };
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var idx = (int)(sorted.Length * p);
        return sorted[Math.Min(idx, sorted.Length - 1)];
    }
}

public sealed class LoadTestSnapshot
{
    public double DurationSeconds { get; init; }
    public long TotalSuccess { get; init; }
    public long TotalFail { get; init; }
    public long TotalBytes { get; init; }
    public double Qps { get; init; }
    public double ThroughputMBps { get; init; }
    public double LatencyP50 { get; init; }
    public double LatencyP90 { get; init; }
    public double LatencyP95 { get; init; }
    public double LatencyP99 { get; init; }
    public double LatencyP999 { get; init; }
    public double LatencyMin { get; init; }
    public double LatencyMax { get; init; }
    public double SuccessRate { get; init; }
    public double RecentQps { get; init; }
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.6 压测报告生成器

**操作**：新建 `DeviceSimulator/Infrastructure/LoadTestReporter.cs`，粘贴以下内容：

```csharp
namespace DeviceSimulator.Infrastructure;

internal static class LoadTestReporter
{
    public static void PrintReport(LoadTestSnapshot s, SimulatorOptions o)
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("              IoTHunter 全链路压测报告");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  场景: {o.Scenario}  协议: {o.Protocol.ToUpper()}  设备数: {o.DeviceCount}");
        Console.WriteLine($"  目标速率: {o.MessagesPerSecond} msg/s/设备  理论总 QPS: {o.DeviceCount * o.MessagesPerSecond:F1}");
        Console.WriteLine($"  持续: {o.DurationSeconds}s  预热: {o.WarmupSeconds}s");
        Console.WriteLine();
        Console.WriteLine("【接入层指标】");
        var total = s.TotalSuccess + s.TotalFail;
        Console.WriteLine($"  总发送: {total:N0}  成功: {s.TotalSuccess:N0}  失败: {s.TotalFail:N0}");
        Console.WriteLine($"  成功率: {s.SuccessRate:F2}%  实际 QPS: {s.Qps:F1}");
        Console.WriteLine($"  P50: {s.LatencyP50:F1}ms  P99: {s.LatencyP99:F1}ms  P999: {s.LatencyP999:F1}ms");
        Console.WriteLine($"  吞吐量: {s.ThroughputMBps:F2} MB/s");
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
    }

    public static void PrintProgress(LoadTestSnapshot s)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] sent={s.TotalSuccess + s.TotalFail,6} succ={s.TotalSuccess,6} fail={s.TotalFail,4} qps={s.RecentQps,6:F1} p99={s.LatencyP99,6:F1}ms rate={s.SuccessRate,6:F2}%");
    }
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.7 HTTP 发送器

**操作**：新建 `DeviceSimulator/Infrastructure/HttpTelemetrySender.cs`，粘贴以下内容。

**说明（F-02）**：组装 POST body 时 **`payload`** 必须为 **`JsonSerializer.Deserialize<JsonElement>(envelope.PayloadJson)`**，以便序列化后与网关 **`TelemetryRequest`** 的 **`JsonElement? Payload`** 形态一致；不得使用 `PayloadJson` 原始字符串作为 `payload` 属性值。  
**依赖**：`SendResult` 枚举位于 `DeviceSimulator/Infrastructure/SendResult.cs`（与仓库一致；若按本计划从零搭建请同步添加该文件）。

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using IoTHunter.Shared.Domain;
using IoTHunter.Shared.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DeviceSimulator.Infrastructure;

internal sealed class HttpTelemetrySender
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<HttpTelemetrySender> _logger;

    public HttpTelemetrySender(IHttpClientFactory httpFactory, ILogger<HttpTelemetrySender> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<SendResult> SendAsync(TelemetryEnvelope envelope, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("Gateway");

        var body = JsonSerializer.Serialize(new
        {
            deviceId = envelope.DeviceId,
            metricType = envelope.MetricType,
            payload = JsonSerializer.Deserialize<JsonElement>(envelope.PayloadJson),
            timestamp = envelope.RecordedAt.ToUnixTimeMilliseconds(),
            sequence = envelope.Sequence
        }, SerializerSetup.TightOptions);

        try
        {
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var endpoint = envelope.ReliabilityLevel == ReliabilityLevel.Critical
                ? "/api/v1/events/critical"
                : "/api/v1/telemetry";

            var response = await client.PostAsync(endpoint, content, ct);

            if (response.StatusCode == HttpStatusCode.Accepted)
                return SendResult.Success;
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                return SendResult.RetryableFailure;
            if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                return SendResult.FatalFailure;
            return SendResult.RetryableFailure;
        }
        catch (TaskCanceledException)
        {
            return SendResult.RetryableFailure;
        }
        catch (HttpRequestException)
        {
            return SendResult.RetryableFailure;
        }
    }
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.8 MQTT 发送器

**操作**：新建 `DeviceSimulator/Infrastructure/MqttTelemetrySender.cs`，粘贴以下内容。

**说明（F-02）**：与 §1.7 相同，MQTT 消息体（JSON 字符串）中的 **`payload`** 必须为 **`JsonSerializer.Deserialize<JsonElement>(envelope.PayloadJson)`**，并与 HTTP 侧字段名一致（`deviceId`、`metricType`、`payload`、`timestamp`、`sequence`），以便网关按 **`TelemetryRequest`** 解析。  
**依赖**：`SendResult` 同 §1.7。

```csharp
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

        var clientId = $"{clientIdPrefix}-{Guid.NewGuid():N}";

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(broker, port)
            .WithClientId(clientId)
            .WithCleanSession(false)
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
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.9 压测核心 Worker（V8.0 Sender 实例复用）

**操作**：新建 `DeviceSimulator/Workers/SimulatorWorker.cs`，粘贴以下完整代码。**此版本在初始化时创建 Sender 实例并复用，重试时不再每次新建连接。**

```csharp
using System.Threading.Channels;
using IoTHunter.Shared.Domain;
using DeviceSimulator.Data;
using DeviceSimulator.Infrastructure;

namespace DeviceSimulator.Workers;

internal sealed class SimulatorWorker : BackgroundService
{
    private readonly SimulatorOptions _options;
    private readonly LoadTestMetrics _metrics;
    private readonly ILogger<SimulatorWorker> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpTelemetrySender> _httpSenderLogger;
    private readonly ILogger<MqttTelemetrySender> _mqttSenderLogger;

    public SimulatorWorker(
        SimulatorOptions options,
        LoadTestMetrics metrics,
        ILogger<SimulatorWorker> logger,
        IHttpClientFactory httpClientFactory,
        ILogger<HttpTelemetrySender> httpSenderLogger,
        ILogger<MqttTelemetrySender> mqttSenderLogger)
    {
        _options = options;
        _metrics = metrics;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _httpSenderLogger = httpSenderLogger;
        _mqttSenderLogger = mqttSenderLogger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // V8.0: 在初始化时创建 Sender 实例并复用
        var httpSender = new HttpTelemetrySender(_httpClientFactory, _httpSenderLogger);
        var mqttSender = new MqttTelemetrySender(_options, _mqttSenderLogger);
        if (_options.Protocol == "mqtt") await mqttSender.ConnectAsync(ct);

        var channel = Channel.CreateBounded<TelemetryEnvelope>(new BoundedChannelOptions(_options.HttpConnections * 200));
        var generator = new TelemetryGenerator(_options.CriticalEventRatio);
        var warmupEnd = DateTime.UtcNow.AddSeconds(_options.WarmupSeconds);
        var endTime = warmupEnd.AddSeconds(_options.DurationSeconds);
        var warmupDone = false;

        // Consumers
        var consumers = Enumerable.Range(0, _options.HttpConnections).Select(async _ =>
        {
            await foreach (var envelope in channel.Reader.ReadAllAsync(ct))
            {
                if (ct.IsCancellationRequested) break;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                SendResult result;
                if (_options.Protocol == "http")
                    result = await httpSender.SendAsync(envelope, ct);
                else
                    result = await mqttSender.SendAsync(envelope, ct);
                sw.Stop();
                if (!warmupDone) continue;
                await ProcessResult(envelope, result, sw.Elapsed.TotalMilliseconds, httpSender, mqttSender, ct);
            }
        }).ToList();

        // Producer
        var deviceIds = Enumerable.Range(1, _options.DeviceCount).Select(i => $"dev-{i:D6}").ToList();
        var producer = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && DateTime.UtcNow < endTime)
            {
                if (!warmupDone && DateTime.UtcNow >= warmupEnd)
                {
                    warmupDone = true;
                    _logger.LogInformation("Warmup complete, metrics collection started.");
                }
                foreach (var deviceId in deviceIds)
                {
                    var envelope = generator.Generate(deviceId);
                    await channel.Writer.WriteAsync(envelope, ct);
                }
                await Task.Delay(TimeSpan.FromSeconds(1.0 / _options.MessagesPerSecond), ct);
            }
            channel.Writer.Complete();
        }, ct);

        // Progress reporting
        while (!ct.IsCancellationRequested && DateTime.UtcNow < endTime)
        {
            await Task.Delay(5000, ct);
            LoadTestReporter.PrintProgress(_metrics.GetSnapshot());
        }

        await Task.WhenAll(consumers);
        await producer;

        // 清理 MQTT 连接
        if (_options.Protocol == "mqtt") await mqttSender.DisposeAsync();

        LoadTestReporter.PrintReport(_metrics.GetSnapshot(), _options);
    }

    private async Task ProcessResult(TelemetryEnvelope envelope, SendResult result, double latencyMs,
        HttpTelemetrySender httpSender, MqttTelemetrySender mqttSender, CancellationToken ct)
    {
        if (result == SendResult.Success)
        {
            _metrics.RecordSuccess(latencyMs, envelope.PayloadJson.Length);
            return;
        }

        // 重试逻辑（复用传入的 Sender 实例）
        bool success = false;
        for (int i = 1; i <= _options.MaxRetries && result == SendResult.RetryableFailure; i++)
        {
            await Task.Delay(_options.RetryBaseDelayMs * (1 << (i - 1)), ct);
            if (_options.Protocol == "http")
                result = await httpSender.SendAsync(envelope, ct);
            else
                result = await mqttSender.SendAsync(envelope, ct);

            if (result == SendResult.Success)
            {
                _metrics.RecordSuccess(latencyMs + _options.RetryBaseDelayMs * (1 << (i - 1)), envelope.PayloadJson.Length);
                success = true;
                break;
            }
        }
        if (!success) _metrics.RecordFailure();
    }
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.10 Program.cs（集成 Worker + UI 端点）

**操作**：完全替换 `DeviceSimulator/Program.cs`，粘贴以下内容。**阶段 4 / UI 边界**：暴露三个只读端点 — **`GET /api/simulator/health`**（存活）、**`GET /api/simulator/config`**（当前 `SimulatorOptions` 与压测目标摘要）、**`GET /api/simulator/snapshot`**（`SimulatorMetrics.GetSnapshot()`，返回 `SimulatorSnapshot` JSON，含 TotalSent、TotalFailed、SuccessRate、Qps、LatencyP99Ms、LatencyMaxMs）。另映射 **`MapPrometheusScrapingEndpoint()`** 供 Prometheus 抓取（与 `Kestrel:Endpoints:Metrics` 端口一致）。

```csharp
using DeviceSimulator;
using DeviceSimulator.Infrastructure.Metrics;
using DeviceSimulator.Infrastructure.Options;
using IoTHunter.Infrastructure;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) =>
{
    LoggingDefaults.ConfigureBaseLogger(configuration);
});

// 标准 Options 绑定
builder.Services.Configure<SimulatorOptions>(
    builder.Configuration.GetSection("Simulator"));

// CLI 参数覆盖（PostConfigure 在 Configure 之后运行）
builder.Services.PostConfigure<SimulatorOptions>(opts =>
{
    var cmd = (string?)null;
    cmd = args.FirstOrDefault(a => a.StartsWith("--protocol="))?.Split('=')[1];
    if (cmd is not null) opts.Protocol = cmd;
    if (int.TryParse(args.FirstOrDefault(a => a.StartsWith("--devices="))?.Split('=')[1], out var dc)) opts.DeviceCount = dc;
    if (int.TryParse(args.FirstOrDefault(a => a.StartsWith("--interval="))?.Split('=')[1], out var iv)) opts.IntervalMs = iv;
    if (int.TryParse(args.FirstOrDefault(a => a.StartsWith("--concurrency="))?.Split('=')[1], out var cc)) opts.Concurrency = cc;
    if (int.TryParse(args.FirstOrDefault(a => a.StartsWith("--duration="))?.Split('=')[1], out var ds)) opts.DurationSeconds = ds;
});

// 命名 HttpClient：ProxyUrl 非空时走边缘代理，否则直连网关
var proxyUrl = builder.Configuration["Simulator:ProxyUrl"];
var httpBase = builder.Configuration["Simulator:GatewayHttpBase"]
               ?? "http://localhost:5080";
var effectiveBase = string.IsNullOrWhiteSpace(proxyUrl) ? httpBase : proxyUrl;
builder.Services.AddHttpClient("Gateway", client =>
{
    client.BaseAddress = new Uri(effectiveBase);
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddSingleton<SimulatorMetrics>();

builder.Services.AddIoTHunterOpenTelemetry(builder.Configuration, "DeviceSimulator")
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter("DeviceSimulator")
            .AddPrometheusExporter();
    });

builder.Services.AddHostedService<SimulatorWorker>();

var app = builder.Build();

app.UseRouting();
app.MapPrometheusScrapingEndpoint();
app.MapGet("/", () => "DeviceSimulator OK");
app.MapGet("/api/simulator/health", () => Results.Ok(new { status = "running" }));
app.MapGet("/api/simulator/config", (IOptions<SimulatorOptions> opts) => Results.Ok(new
{
    opts.Value.Protocol,
    Target = opts.Value.Protocol.Equals("mqtt", StringComparison.OrdinalIgnoreCase)
        ? opts.Value.MqttWebSocketUrl
        : opts.Value.GatewayHttpBase,
    opts.Value.DeviceCount,
    opts.Value.IntervalMs,
    opts.Value.Concurrency,
    opts.Value.DurationSeconds,
    opts.Value.CriticalEventRatio
}));
app.MapGet("/api/simulator/snapshot", (SimulatorMetrics metrics) =>
    Results.Ok(metrics.GetSnapshot()));

await app.RunAsync();
```

**同时更新 `DeviceSimulator/appsettings.json`**（与仓库对齐；按需改端口）：

```json
{
  "OpenTelemetry": {
    "OtlpEndpoint": ""
  },
  "Simulator": {
    "Protocol": "http",
    "DeviceCount": 10,
    "IntervalMs": 1000,
    "Concurrency": 4,
    "DurationSeconds": 30,
    "CriticalEventRatio": 0.05,
    "GatewayHttpBase": "http://localhost:5080",
    "MqttWebSocketUrl": "ws://localhost:8083/mqtt",
    "ProxyUrl": ""
  },
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://+:80" },
      "Metrics": { "Url": "http://+:9464" }
    }
  }
}
```

**说明**：默认 **Web** 监听 **`http://+:80`**。若下文 E2E / 脚本仍使用 **5091**，可将 `Http.Url` 改为 `http://0.0.0.0:5091`，或容器使用 **`-p 5091:80`** 后 `curl http://localhost:5091/api/simulator/...`。

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.11 Dockerfile（安装 curl，支持健康检查）

**操作**：新建 `DeviceSimulator/Dockerfile`，粘贴以下内容：

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish DeviceSimulator/DeviceSimulator.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:5091
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
EXPOSE 5091
ENTRYPOINT ["dotnet", "DeviceSimulator.dll"]
```

**验证**：`docker build -t device-simulator:step4 -f DeviceSimulator/Dockerfile .` 成功。☐

---

### 1.12 自动化验证脚本

**操作**：新建 `scripts/verify-step4.sh`，粘贴以下内容：

```bash
#!/bin/bash
set -euo pipefail
echo "=== Step 4 Auto Verification ==="

echo -n "Build... "; dotnet build DeviceSimulator/DeviceSimulator.csproj > /dev/null; echo "OK"
echo -n "Docker build... "; docker build -t device-simulator:step4 -f DeviceSimulator/Dockerfile . > /dev/null; echo "OK"

echo -n "Start Simulator... "
docker run -d --name sim-step4 -p 5091:5091 device-simulator:step4
sleep 5
echo "OK"

echo -n "Health... "; curl -sf http://localhost:5091/api/simulator/health | grep -q '"status":"running"'; echo "OK"
echo -n "Config... "; curl -sf http://localhost:5091/api/simulator/config | grep -q '"Protocol"'; echo "OK"

echo -n "Snapshot... "
for i in $(seq 1 10); do
  SNAP=$(curl -sf http://localhost:5091/api/simulator/snapshot)
  if echo "$SNAP" | grep -q '"totalSuccess"'; then
    echo "OK"
    break
  fi
  if [ $i -eq 10 ]; then echo "FAIL: $SNAP"; exit 1; fi
  sleep 2
done

docker rm -f sim-step4 > /dev/null 2>&1 || true
echo "=== All Step 4 checks passed ==="
```

**验证**：`chmod +x scripts/verify-step4.sh`。☐

---

## 2. Live E2E 验证剧本（V8.0 增强版）

> **前置条件**：所有中间件已启动，IoTGateway 在运行，BackendProcessor 在运行。

| 编号 | 验证场景       | 操作步骤                                               | 预期结果                                        | 通过 |
| ---- | -------------- | ------------------------------------------------------ | ----------------------------------------------- | ---- |
| 4.1  | 编译           | `dotnet build DeviceSimulator/DeviceSimulator.csproj`  | 0 Error, 0 Warning                              | ☐    |
| 4.2  | 基准测试       | 配置 `Scenario=baseline`，启动 Simulator，等待自动结束 | 控制台输出完整报告，成功率 > 99%                | ☐    |
| 4.3  | 连接数上限测试 | 配置 `Scenario=connection-limit, DeviceCount=1000`     | MQTT 连接建立无崩溃，日志显示连接状态           | ☐    |
| 4.4  | 吞吐量测试     | 配置 `Scenario=throughput`                             | QPS 接近理论值，报告含 Kafka Lag 和 PG TPS 采样 | ☐    |
| 4.5  | 实时进度输出   | 运行中                                                 | 每 5 秒打印 sent/succ/fail/qps/p99/rate         | ☐    |
| 4.6  | 压测报告完整性 | 压测结束                                               | 报告含总发送/成功/失败/QPS/P50/P99/P999         | ☐    |
| 4.7  | UI 健康检查    | `curl http://localhost:5091/api/simulator/health`      | `{"status":"running"}`                          | ☐    |
| 4.8  | UI 配置查询    | `curl http://localhost:5091/api/simulator/config`      | 包含 Protocol、Target、DeviceCount 等字段       | ☐    |
| 4.9  | UI 快照数据    | `curl http://localhost:5091/api/simulator/snapshot`    | 返回 **`SimulatorSnapshot`** JSON（TotalSent、TotalFailed、SuccessRate、Qps、LatencyP99Ms、LatencyMaxMs） | ☐    |
| 4.10 | 数据闭环校验   | 吞吐量测试后查 PG 记录数与 Simulator 成功量            | 偏差 < 2%                                       | ☐    |

---

## 3. 人工测试操作指南

### 3.1 环境准备

```bash
docker compose -f docker-compose.local.yml up -d
# 确认 IoTGateway(5080) 和 BackendProcessor(5081) 在运行
```

### 3.2 编译与启动

```bash
cd IoTHunter
dotnet build DeviceSimulator/DeviceSimulator.csproj
dotnet run --project DeviceSimulator/DeviceSimulator.csproj
```

Simulator 将监听 `http://localhost:5091`，压测 Worker 自动启动。

### 3.3 验证 UI 接口

```bash
curl http://localhost:5091/api/simulator/health
curl http://localhost:5091/api/simulator/config
curl http://localhost:5091/api/simulator/snapshot
```

**预期**：health 返回 `{"status":"running"}`；config 返回压测参数；snapshot 返回动态指标。☐

### 3.4 执行各压测场景

**基准测试**：修改 `appsettings.json` 中 `Scenario=baseline`，重启 Simulator，等待 30 秒自动停止，观察报告成功率 > 99%。☐

**连接数上限测试**：修改 `Scenario=connection-limit, Protocol=mqtt, DeviceCount=1000`，观察日志中 MQTT 连接建立状态，无崩溃。☐

**吞吐量测试**：修改 `Scenario=throughput`，启动后另开终端持续执行 `curl http://localhost:5091/api/simulator/snapshot` 观察 QPS 上升；等待 300 秒结束，核对报告中的实际 QPS 和 P99 延迟。☐

### 3.5 数据闭环核对

压测结束后，等待 10~20 秒让 BackendProcessor 消费完：

```bash
docker compose -f docker-compose.local.yml exec postgres psql -U iotapp -d IoTHunter -c "SELECT COUNT(*) FROM telemetry_records;"
```

与 Simulator 报告中的 `TotalSuccess` 对比，偏差应 < 2%。☐

### 3.6 收尾

- 保存终端输出至 `screenshots/step4/`。
- 停止 Simulator：`Ctrl+C`。

---

## 4. 完成标准

- [ ] Simulator 是 Web 宿主，暴露 `/api/simulator/health`、`/config`、`/snapshot` 端点
- [ ] 支持四种标准压测场景（baseline / connection-limit / throughput / stability）
- [ ] HTTP 模式支持连接池，MQTT 模式支持长连接发送，**Sender 实例在 Worker 生命周期内复用**
- [ ] 多维度指标：QPS、成功率、P50/P90/P95/P99/P999、吞吐量
- [ ] 压测结束自动打印完整结构化报告
- [ ] 数据闭环校验偏差 < 2%
- [ ] Live 验证 10 项全部通过
- [ ] 无硬编码连接串、Topic、QoS，所有配置来自 `SimulatorOptions`
- [ ] 无 Mock 数据——所有验证使用真实中间件
- [ ] `IoTHunter.Infrastructure` 被正确引用

---

第四步开发计划 V8.0 最终版完毕。D爷，Sender复用已集成，curl已安装，UI端点齐备，压测能力专业，所有裁决已熔接。请定稿。