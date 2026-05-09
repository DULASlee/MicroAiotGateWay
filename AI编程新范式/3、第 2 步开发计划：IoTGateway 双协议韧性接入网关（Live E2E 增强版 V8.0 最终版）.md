# 第 2 步开发计划：IoTGateway 双协议韧性接入网关（Live E2E 增强版 V8.0 最终版）

> **版本**：V8.0（熔接两次裁决：Shared/Infrastructure 分离、OTel 全链路注册、Polly Pipeline DI 注入、MQTT 指数退避重连、Dockerfile 安装 curl、验证脚本彻底移除 host 网络）  
> **对应总架构**：`架构设计文档 V7.0` 第 2 步  
> **前置依赖**：第 1 步 V8.0 全部 Live 验证通过，`IoTHunter.Infrastructure` 类库已创建并包含 `OpenTelemetryDefaults`（已注册所有自定义 ActivitySource），中间件容器仍在运行  
> **本步目标**：实现 IoTGateway 的 HTTP 与 MQTT WebSocket 双协议接入能力，以 **Kafka produce ack** 为唯一可靠边界，Polly 韧性管道（DI 注入，禁止静态 LoggerFactory）包裹写入，W3C Trace Context 通过 Kafka Header 跨进程传播。**为 UI 运营控制台预留 `GET /api/v1/config` 端点。** **从第 1 天起即支持容器化运行。**  
> **本步边界**：只实现网关接入层和自身配置透出，不实现登录认证、设备凭证管理等管理功能（第 9 步 Management 微服务负责）。  
> **核心禁令**：  
> - **硬编码禁令（ADR-022）**：所有连接串、Topic 名称、QoS 配置必须通过 `IConfiguration` 注入。  
> - **Mock 数据禁令（ADR-023）**：验证必须使用真实的 Kafka、Mosquitto 中间件。  
> - **UI 边界（ADR-024）**：`/api/v1/config` 仅返回网关自身运行态信息。  
> **【V8.0 关键修正】**：  
> - **Polly Pipeline DI 化**：`KafkaResiliencePipeline` 为非静态类，通过 DI 注入 `ILogger<KafkaResiliencePipeline>`，杜绝每次重试创建 `LoggerFactory`。  
> - **MQTT 原子化重连 + 指数退避**：使用 `_mqttOptions.RetryBaseDelayMs * (1 << Math.Min(attempt - 1, 5))`，最大 3.2s，替代固定间隔。  
> - **Dockerfile 安装 curl**：在 runtime 阶段执行 `apt-get install -y curl`，确保健康检查可用。  
> - **验证脚本彻底跨平台**：完全移除 `--network host`，Gateway 容器映射端口 `-p 5080:80`。  
> - **引用 Infrastructure 类库**：所有服务项目均引用 `IoTHunter.Infrastructure`，`using IoTHunter.Infrastructure;`。

---

## 0. 前置检查

| 检查项                                      | 验证命令                                                     | 状态要求                                                     |
| ------------------------------------------- | ------------------------------------------------------------ | ------------------------------------------------------------ |
| 全部中间件 Running                          | `docker ps`                                                  | kafka、mosquitto、postgres、timescaledb、redis、zookeeper 六个容器 Running |
| 第 1 步 V8.0 验证通过                       | `screenshots/step1/` 下存在全部验证证据                      | 14 项剧本已勾选                                              |
| `IoTHunter.Infrastructure` 类库存在         | `dotnet build IoTHunter.Infrastructure/IoTHunter.Infrastructure.csproj` | 0 Error, 0 Warning                                           |
| `OpenTelemetryDefaults` 已注册自定义 Source | 检查 `IoTHunter.Infrastructure/OpenTelemetryDefaults.cs`     | 包含 `AddSource("IoTGateway.Kafka")` 等调用                  |
| IoTGateway 可编译                           | `dotnet build IoTGateway/IoTGateway.csproj`                  | 0 Error, 0 Warning                                           |

---

## 1. 动作项

### 1.1 更新 IoTGateway 配置文件

**操作**：完全替换 `IoTGateway/appsettings.json` 为以下内容。

```json
{
  "AllowedHosts": "*",
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:80"
      }
    }
  },
  "Kafka": {
    "BootstrapServers": "kafka:9092",
    "MessageTimeoutMs": 5000,
    "ClientId": "iot-gateway",
    "MaxInFlight": 5
  },
  "Mqtt": {
    "WebSocketUrl": "ws://mosquitto:8083/mqtt",
    "TcpServer": "mosquitto",
    "TcpPort": 1883,
    "ClientId": "iot-gateway-mqtt",
    "Username": "",
    "Password": "",
    "AutoReconnectDelayMs": 5000,
    "RetryBaseDelayMs": 100
  },
  "OpenTelemetry": {
    "OtlpEndpoint": ""
  }
}
```

**设计说明**：新增 `RetryBaseDelayMs` 字段，供 MQTT 指数退避使用。`Username`/`Password` 为空，兼容本地匿名访问，容器化后通过环境变量注入凭证。  
**验证**：`cat IoTGateway/appsettings.json` 确认内容正确。☐

**项目引用（必须，V8.0 / ADR-027）**：`IoTGateway.csproj` 必须同时引用 **`IoTHunter.Shared`**（领域模型、统一 `SerializerSetup`）与 **`IoTHunter.Infrastructure`**（`LoggingDefaults`、`OpenTelemetryDefaults`）。若第 1 步已添加可跳过；否则执行：

```powershell
dotnet add IoTGateway/IoTGateway.csproj reference IoTHunter.Shared/IoTHunter.Shared.csproj
dotnet add IoTGateway/IoTGateway.csproj reference IoTHunter.Infrastructure/IoTHunter.Infrastructure.csproj
```

**验证**：打开 `IoTGateway/IoTGateway.csproj`，确认存在两条 `<ProjectReference>`，分别指向上述两个项目。☐

---

### 1.2 定义选项类

**操作**：依次新建以下两个文件，每新建一个后执行 `dotnet build IoTGateway/IoTGateway.csproj` 确认无编译错误。

#### 1.2.1 KafkaOptions

新建 `IoTGateway/Infrastructure/Options/KafkaOptions.cs`，粘贴以下内容：

```csharp
namespace IoTGateway.Infrastructure.Options;

public sealed class KafkaOptions
{
    public string BootstrapServers { get; set; } = "kafka:9092";
    public int MessageTimeoutMs { get; set; } = 5000;
    public string ClientId { get; set; } = "iot-gateway";
    public int MaxInFlight { get; set; } = 5;
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

#### 1.2.2 MqttOptions

新建 `IoTGateway/Infrastructure/Options/MqttOptions.cs`，粘贴以下内容：

```csharp
namespace IoTGateway.Infrastructure.Options;

public sealed class MqttOptions
{
    public string WebSocketUrl { get; set; } = "ws://mosquitto:8083/mqtt";
    public string TcpServer { get; set; } = "mosquitto";
    public int TcpPort { get; set; } = 1883;
    public string ClientId { get; set; } = "iot-gateway-mqtt";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int AutoReconnectDelayMs { get; set; } = 5000;
    public int RetryBaseDelayMs { get; set; } = 100;
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.3 安装 NuGet 依赖（IoTGateway，版本与仓库对齐）

**说明**：本节对应终审报告 **4.2.11 配置依赖清单**；版本号须与当前仓库 `IoTGateway/IoTGateway.csproj` 一致。新增依赖时**一律**使用 `dotnet add`，禁止手工抄写易过期版本号。

**推荐命令（逐条执行，由 NuGet 解析具体版本）**：

```powershell
dotnet add IoTGateway/IoTGateway.csproj package Confluent.Kafka
dotnet add IoTGateway/IoTGateway.csproj package Microsoft.AspNetCore.OpenApi
dotnet add IoTGateway/IoTGateway.csproj package MQTTnet
dotnet add IoTGateway/IoTGateway.csproj package OpenTelemetry
dotnet add IoTGateway/IoTGateway.csproj package OpenTelemetry.Exporter.OpenTelemetryProtocol
dotnet add IoTGateway/IoTGateway.csproj package OpenTelemetry.Exporter.Prometheus.AspNetCore --prerelease
dotnet add IoTGateway/IoTGateway.csproj package OpenTelemetry.Extensions.Hosting
dotnet add IoTGateway/IoTGateway.csproj package OpenTelemetry.Instrumentation.AspNetCore
dotnet add IoTGateway/IoTGateway.csproj package OpenTelemetry.Instrumentation.Http
dotnet add IoTGateway/IoTGateway.csproj package OpenTelemetry.Instrumentation.Runtime
dotnet add IoTGateway/IoTGateway.csproj package Polly.Core
dotnet add IoTGateway/IoTGateway.csproj package Serilog
dotnet add IoTGateway/IoTGateway.csproj package Serilog.AspNetCore
dotnet add IoTGateway/IoTGateway.csproj package Serilog.Extensions.Hosting
dotnet add IoTGateway/IoTGateway.csproj package Serilog.Sinks.Console
```

**截至当前仓库的锁定版本（便于对照 `IoTGateway.csproj`）**：

| 包名 | 版本 |
|------|------|
| Confluent.Kafka | 2.14.0 |
| Microsoft.AspNetCore.OpenApi | 10.0.6 |
| MQTTnet | 5.1.0.1559 |
| OpenTelemetry | 1.15.3 |
| OpenTelemetry.Exporter.OpenTelemetryProtocol | 1.15.3 |
| OpenTelemetry.Exporter.Prometheus.AspNetCore | 1.15.3-beta.1 |
| OpenTelemetry.Extensions.Hosting | 1.15.3 |
| OpenTelemetry.Instrumentation.AspNetCore | 1.15.2 |
| OpenTelemetry.Instrumentation.Http | 1.15.1 |
| OpenTelemetry.Instrumentation.Runtime | 1.15.1 |
| Polly.Core | 8.6.6 |
| Serilog | 4.3.1 |
| Serilog.AspNetCore | 10.0.0 |
| Serilog.Extensions.Hosting | 10.0.0 |
| Serilog.Sinks.Console | 6.1.1 |

**验证**：`dotnet restore` 成功，`dotnet build IoTGateway/IoTGateway.csproj` 0 Error, 0 Warning。☐

---

### 1.4 定义 HTTP 接入 DTO（含校验）

**操作**：新建 `IoTGateway/Contracts/TelemetryRequest.cs`，粘贴以下内容：

```csharp
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IoTGateway.Contracts;

public sealed record TelemetryRequest(
    [property: JsonPropertyName("deviceId")]
    [Required(ErrorMessage = "deviceId is required")]
    [MinLength(1, ErrorMessage = "deviceId must not be empty")]
    string DeviceId,

    [property: JsonPropertyName("metricType")]
    [Required(ErrorMessage = "metricType is required")]
    string MetricType,

    [property: JsonPropertyName("payload")]
    JsonElement? Payload,

    [property: JsonPropertyName("timestamp")]
    [Range(1, long.MaxValue, ErrorMessage = "timestamp must be a positive Unix ms value")]
    long Timestamp,

    [property: JsonPropertyName("sequence")]
    [Range(0, long.MaxValue, ErrorMessage = "sequence must be non-negative")]
    long Sequence
);
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.5 统一 JSON 序列化配置

**说明**：本项目统一使用 `IoTHunter.Shared.Infrastructure.SerializerSetup.TightOptions` 进行 JSON 序列化/反序列化，禁止各服务自行定义序列化配置。Gateway 项目中已通过 `IoTHunter.Shared` 依赖自动获得此配置。

**验证**：确认 `IoTHunter.Shared/Infrastructure/SerializerSetup.cs` 存在。☐

---

### 1.6 定义 Envelope 映射器（使用工厂方法）

**操作**：新建 `IoTGateway/Infrastructure/Messaging/TelemetryEnvelopeMapper.cs`，粘贴以下内容：

```csharp
using System.Text.Json;
using IoTHunter.Shared.Domain;
using IoTHunter.Shared.Infrastructure;
using IoTGateway.Contracts;

namespace IoTGateway.Infrastructure.Messaging;

internal static class TelemetryEnvelopeMapper
{
    public static TelemetryEnvelope ToEnvelope(TelemetryRequest request, ReliabilityLevel level)
    {
        return TelemetryEnvelope.Create(
            eventId: $"{request.DeviceId}:{request.MetricType}:{request.Sequence}:{request.Timestamp}",
            deviceId: request.DeviceId,
            metricType: request.MetricType,
            recordedAt: DateTimeOffset.FromUnixTimeMilliseconds(request.Timestamp),
            payloadJson: request.Payload.HasValue
                ? JsonSerializer.Serialize(request.Payload.Value, SerializerSetup.TightOptions)
                : "{}",
            reliabilityLevel: level,
            sequence: request.Sequence);
    }
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.7 构建 Polly 韧性管道（V8.0 DI 注入版本）

**操作**：新建 `IoTGateway/Infrastructure/Resilience/KafkaResiliencePipeline.cs`，粘贴以下内容。**注意类名和构造方式已变，Program.cs 中将注册为 Singleton**。

```csharp
using Confluent.Kafka;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace IoTGateway.Infrastructure.Resilience;

public sealed class KafkaResiliencePipeline
{
    private readonly ILogger<KafkaResiliencePipeline> _logger;

    public KafkaResiliencePipeline(ILogger<KafkaResiliencePipeline> logger)
    {
        _logger = logger;
    }

    public ResiliencePipeline<DeliveryResult<Null, string>> Build()
    {
        return new ResiliencePipelineBuilder<DeliveryResult<Null, string>>()
            .AddRetry(new RetryStrategyOptions<DeliveryResult<Null, string>>
            {
                ShouldHandle = new PredicateBuilder<DeliveryResult<Null, string>>()
                    .Handle<ProduceException<Null, string>>()
                    .Handle<TimeoutRejectedException>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(100),
                BackoffType = DelayBackoffType.Exponential,
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        "Kafka produce retry {Attempt}/{MaxAttempts} after {DelayMs}ms",
                        args.AttemptNumber, 3, args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(4))
            .Build();
    }
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.8 实现 Kafka Producer 服务（含 W3C Trace 注入）

**操作**：新建 `IoTGateway/Infrastructure/Messaging/KafkaProducerService.cs`，粘贴以下内容：

```csharp
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using IoTHunter.Shared.Domain;
using IoTHunter.Shared.Infrastructure;
using IoTGateway.Infrastructure.Options;
using IoTGateway.Infrastructure.Resilience;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace IoTGateway.Infrastructure.Messaging;

public sealed class KafkaProducerService : IDisposable
{
    private static readonly ActivitySource ActivitySource = new("IoTGateway.Kafka");
    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    private readonly IProducer<Null, string> _producer;
    private readonly ResiliencePipeline<DeliveryResult<Null, string>> _pipeline;
    private readonly ILogger<KafkaProducerService> _logger;

    public KafkaProducerService(
        KafkaOptions options,
        KafkaResiliencePipeline pipeline,
        ILogger<KafkaProducerService> logger)
    {
        _pipeline = pipeline.Build();
        _logger = logger;

        var config = new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MaxInFlight = options.MaxInFlight,
            MessageSendMaxRetries = 3,
            MessageTimeoutMs = options.MessageTimeoutMs,
            ClientId = options.ClientId,
            SocketKeepaliveEnable = true
        };

        _producer = new ProducerBuilder<Null, string>(config).Build();
        _logger.LogInformation("Kafka producer initialized: {BootstrapServers} client={ClientId}",
            options.BootstrapServers, options.ClientId);
    }

    public async Task<DeliveryResult<Null, string>> ProduceAsync(
        string topic, TelemetryEnvelope envelope, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(envelope, SerializerSetup.TightOptions);
        var message = new Message<Null, string> { Value = json, Headers = [] };

        var parentActivity = Activity.Current;
        using var activity = ActivitySource.StartActivity(
            "Kafka.Produce", ActivityKind.Producer, parentActivity?.Context ?? default);

        // ADR-004: Inject W3C Trace Context into Kafka Headers
        Propagator.Inject(
            new PropagationContext(activity?.Context ?? default, Baggage.Current),
            message.Headers,
            (headers, key, value) => headers.Add(key, Encoding.UTF8.GetBytes(value)));

        // Business metadata headers
        message.Headers.Add("event_id", Encoding.UTF8.GetBytes(envelope.EventId));
        message.Headers.Add("schema_version", Encoding.UTF8.GetBytes(envelope.SchemaVersion.ToString()));
        message.Headers.Add("reliability_level", Encoding.UTF8.GetBytes(((int)envelope.ReliabilityLevel).ToString()));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await _pipeline.ExecuteAsync(
                async innerCt => await _producer.ProduceAsync(topic, message, innerCt), ct);

            stopwatch.Stop();
            activity?.SetTag("messaging.system", "kafka");
            activity?.SetTag("messaging.destination", topic);
            activity?.SetTag("messaging.kafka.partition", result.Partition.Value);
            activity?.SetTag("messaging.kafka.offset", result.Offset.Value);

            _logger.LogInformation(
                "Kafka ack {EventId} topic={Topic} partition={Partition} offset={Offset} latency={LatencyMs}ms",
                envelope.EventId, topic, result.Partition.Value, result.Offset.Value, stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex,
                "Kafka produce failed {EventId} topic={Topic} latency={LatencyMs}ms",
                envelope.EventId, topic, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public void Ping()
    {
        using var admin = new DependentAdminClientBuilder(_producer.Handle).Build();
        admin.GetMetadata(TimeSpan.FromSeconds(3));
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.9 MQTT WebSocket 接入服务（V8.0：TaskCompletionSource 断连唤醒 + 指数退避 + 原子化客户端）

**操作**：新建 `IoTGateway/Infrastructure/Mqtt/MqttIngestionService.cs`，粘贴以下完整内容。**要点**：使用 `TaskCompletionSource` 在 `DisconnectedAsync` 中立即唤醒主循环，避免仅靠 `Task.Delay(Timeout.Infinite)` 导致 broker 断连后永不重连；使用 `MqttClientFactory`（MQTTnet 5.x API），**禁止** `MqttFactory`。

```csharp
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IoTHunter.Shared.Domain;
using IoTHunter.Shared.Infrastructure;
using IoTGateway.Contracts;
using IoTGateway.Infrastructure.Messaging;
using IoTGateway.Infrastructure.Metrics;
using IoTGateway.Infrastructure.Options;
using MQTTnet;
using MQTTnet.Protocol;

namespace IoTGateway.Infrastructure.Mqtt;

internal sealed partial class MqttIngestionService : BackgroundService
{
    [GeneratedRegex(@"^device/(?<deviceId>[^/]+)/(?<rest>telemetry|event/critical)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex TopicPattern();

    private readonly KafkaProducerService _producer;
    private readonly MqttOptions _mqttOptions;
    private readonly GatewayMetrics _metrics;
    private readonly ILogger<MqttIngestionService> _logger;
    private IMqttClient? _client;
    private CancellationToken _stoppingToken;

    public MqttIngestionService(
        KafkaProducerService producer,
        MqttOptions mqttOptions,
        GatewayMetrics metrics,
        ILogger<MqttIngestionService> logger)
    {
        _producer = producer;
        _mqttOptions = mqttOptions;
        _metrics = metrics;
        _logger = logger;
    }

    public bool IsConnected => _client?.IsConnected ?? false;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        var factory = new MqttClientFactory();
        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            attempt++;
            var client = factory.CreateMqttClient();
            var disconnectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            client.DisconnectedAsync += e =>
            {
                if (e.ClientWasConnected)
                {
                    _logger.LogWarning("MQTT disconnected: {Reason}", e.Reason);
                    disconnectTcs.TrySetResult(true);
                }
                return Task.CompletedTask;
            };

            client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;

            try
            {
                await ConnectAndSubscribeAsync(client, stoppingToken);
                _client = client;
                attempt = 0; // reset backoff counter on successful connection
                _logger.LogInformation("MQTT ingestion started, TCP={Server}:{Port}",
                    _mqttOptions.TcpServer, _mqttOptions.TcpPort);

                // Wait until disconnect or shutdown — TCS gives instant wake-up on disconnect
                var completed = await Task.WhenAny(disconnectTcs.Task, Task.Delay(Timeout.Infinite, stoppingToken));
                if (completed == disconnectTcs.Task)
                {
                    // Disconnected — clean up and loop back to reconnect
                    _logger.LogWarning("MQTT disconnected, reconnecting...");
                }
                await DisposeClientAsync(client);
                _client = null;

                if (stoppingToken.IsCancellationRequested) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await DisposeClientAsync(client);
                break;
            }
            catch (Exception ex)
            {
                var delayMs = _mqttOptions.RetryBaseDelayMs * (1 << Math.Min(attempt - 1, 5));
                _logger.LogError(ex,
                    "MQTT connection failed (attempt {Attempt}), retrying in {DelayMs}ms",
                    attempt, delayMs);
                await DisposeClientAsync(client);
                try { await Task.Delay(delayMs, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("MQTT ingestion stopping...");
        if (_client is not null)
        {
            await DisposeClientAsync(_client);
        }
    }

    private static async Task DisposeClientAsync(IMqttClient client)
    {
        try { await client.DisconnectAsync(); } catch { }
        client.Dispose();
    }

    private async Task ConnectAndSubscribeAsync(IMqttClient client, CancellationToken ct)
    {
        var clientOptionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(_mqttOptions.TcpServer, _mqttOptions.TcpPort)
            .WithClientId(_mqttOptions.ClientId)
            .WithCleanSession(false)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));

        if (!string.IsNullOrWhiteSpace(_mqttOptions.Username))
        {
            clientOptionsBuilder.WithCredentials(_mqttOptions.Username, _mqttOptions.Password);
        }

        await client.ConnectAsync(clientOptionsBuilder.Build(), ct);

        await client.SubscribeAsync(new MqttTopicFilterBuilder()
            .WithTopic("$share/gateway-group/device/+/telemetry")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .Build(), ct);

        await client.SubscribeAsync(new MqttTopicFilterBuilder()
            .WithTopic("$share/gateway-group/device/+/event/critical")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);

        _logger.LogInformation("MQTT connected and subscribed");
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

        try
        {
            var match = TopicPattern().Match(topic);
            if (!match.Success)
            {
                _logger.LogWarning("MQTT rejected: invalid topic '{Topic}'", topic);
                _metrics.RecordRejection("mqtt", "invalid_topic");
                return;
            }

            var deviceId = match.Groups["deviceId"].Value;
            var isCritical = match.Groups["rest"].Value == "event/critical";

            var request = JsonSerializer.Deserialize<TelemetryRequest>(payload, SerializerSetup.TightOptions);
            if (request is null)
            {
                _logger.LogWarning("MQTT rejected: deserialization failed");
                _metrics.RecordRejection("mqtt", "deserialization_failed");
                return;
            }

            var validationResults = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
            var validationContext = new System.ComponentModel.DataAnnotations.ValidationContext(request);
            if (!System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
                    request, validationContext, validationResults, validateAllProperties: true))
            {
                _logger.LogWarning("MQTT rejected: validation failed {Errors}", string.Join("; ", validationResults));
                _metrics.RecordRejection("mqtt", "validation_failed");
                return;
            }

            var reliability = isCritical ? ReliabilityLevel.Critical : ReliabilityLevel.BestEffort;
            var kafkaTopic = isCritical
                ? ReliabilityConfiguration.KafkaTopics[ReliabilityLevel.Critical]
                : ReliabilityConfiguration.KafkaTopics[ReliabilityLevel.BestEffort];

            var envelope = TelemetryEnvelopeMapper.ToEnvelope(request, reliability);
            await _producer.ProduceAsync(kafkaTopic, envelope, _stoppingToken);

            _metrics.RecordRequest("mqtt", kafkaTopic, "success");
            _metrics.RecordMqttMessage(topic, isCritical ? "qos1" : "qos0");

            _logger.LogInformation("MQTT→Kafka {EventId} device={DeviceId} topic={MqttTopic}→{KafkaTopic} reliability={Level}",
                envelope.EventId, deviceId, topic, kafkaTopic, reliability);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQTT processing failed topic={Topic}", topic);
            _metrics.RecordRejection("mqtt", ex.GetType().Name);
        }
    }
}
```

**验证**：`dotnet build` 0 Error, 0 Warning；故意重启 Mosquitto 后日志出现「 disconnected → reconnecting 」循环，且 HTTP 通道不受影响。☐

---

### 1.10 自定义 Prometheus 指标

**操作**：新建 `IoTGateway/Infrastructure/Metrics/GatewayMetrics.cs`，粘贴以下内容：

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace IoTGateway.Infrastructure.Metrics;

public sealed class GatewayMetrics
{
    private readonly Meter _meter;
    private readonly Counter<long> _requestsTotal;
    private readonly Counter<long> _rejectedTotal;
    private readonly Histogram<double> _kafkaAckLatency;
    private readonly Counter<long> _mqttMessagesTotal;

    public GatewayMetrics()
    {
        _meter = new Meter("IoTGateway", "1.0.0");

        _requestsTotal = _meter.CreateCounter<long>(
            "gateway_requests_total", description: "Total ingestion requests by protocol, topic and status.");
        _rejectedTotal = _meter.CreateCounter<long>(
            "gateway_rejected_total", description: "Total rejected requests by reason.");
        _kafkaAckLatency = _meter.CreateHistogram<double>(
            "gateway_kafka_ack_latency_ms", unit: "ms", description: "End-to-end Kafka produce latency.");
        _mqttMessagesTotal = _meter.CreateCounter<long>(
            "mqtt_messages_total", description: "Total MQTT messages received by topic and qos.");
    }

    public void RecordRequest(string protocol, string topic, string status)
    {
        var tags = new TagList { { "protocol", protocol }, { "topic", topic }, { "status", status } };
        _requestsTotal.Add(1, tags);
    }

    public void RecordRejection(string protocol, string reason)
    {
        var tags = new TagList { { "protocol", protocol }, { "reason", reason } };
        _rejectedTotal.Add(1, tags);
    }

    public void RecordKafkaLatency(string topic, double latencyMs)
    {
        var tags = new TagList { { "topic", topic } };
        _kafkaAckLatency.Record(latencyMs, tags);
    }

    public void RecordMqttMessage(string topic, string qos)
    {
        var tags = new TagList { { "topic", topic }, { "qos", qos } };
        _mqttMessagesTotal.Add(1, tags);
    }
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.11 双通道健康检查

**操作**：新建 `IoTGateway/Infrastructure/Health/KafkaHealthCheck.cs` 和 `MqttHealthCheck.cs`（内容与 V7.0 一致，略）。

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.12 HTTP 接入控制器

**操作**：新建 `IoTGateway/Controllers/TelemetryController.cs`（内容与 V7.0 一致，略）。

**Kafka topic（必须，C-2 / C-3 / ADR-022）**：向 `KafkaProducerService` 传入的 topic 字符串**必须**来自 `ReliabilityConfiguration.KafkaTopics` 字典（例如 `ReliabilityConfiguration.KafkaTopics[ReliabilityLevel.BestEffort]`、`[ReliabilityLevel.Critical]`），**禁止**在代码中硬编码 `"telemetry.raw"`、`"event.critical"` 等字面量（`ConfigController` 透出 topic 列表时亦应基于同一字典）。

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.13 配置查询端点

**操作**：新建 `IoTGateway/Controllers/ConfigController.cs`，注意注入的是 `IOptions<MqttOptions>` 或直接使用已注册的单例。内容与 V7.0 一致。**Kafka 侧透出的 `Topics` 数组**须来自 `ReliabilityConfiguration.KafkaTopics.Values.Distinct()`（与 1.12 一致），禁止硬编码 topic 字符串。

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.14 Dockerfile（安装 curl）

**操作**：新建 `IoTGateway/Dockerfile`，粘贴以下内容：

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish IoTGateway/IoTGateway.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80
ENTRYPOINT ["dotnet", "IoTGateway.dll"]
```

**验证**：`docker build -t iot-gateway:step2 -f IoTGateway/Dockerfile .` 成功。☐

---

### 1.15 自动化验证脚本（彻底移除 host 网络）

**操作**：新建 `scripts/verify-step2.sh`，粘贴以下内容：

```bash
#!/bin/bash
set -euo pipefail
echo "=== Step 2 Auto Verification ==="

echo -n "Starting environment... "
docker compose -f docker-compose.local.yml down --remove-orphans > /dev/null 2>&1 || true
docker compose -f docker-compose.local.yml up -d
echo "done"

echo -n "Build... "
dotnet build IoTGateway/IoTGateway.csproj > /dev/null
echo "OK"

echo -n "Docker build Gateway... "
docker build -t iot-gateway:step2 -f IoTGateway/Dockerfile . > /dev/null
echo "OK"

echo -n "Starting Gateway container... "
docker run -d --name gw-step2 -p 5080:80 iot-gateway:step2
sleep 5
echo "OK"

echo -n "Waiting for Gateway healthy... "
for i in $(seq 1 30); do
  if curl -sf http://localhost:5080/health/ready > /dev/null 2>&1; then
    echo "OK (${i}s)"
    break
  fi
  if [ $i -eq 30 ]; then
    echo "FAIL (timeout)"
    docker logs gw-step2
    exit 1
  fi
  sleep 1
done

echo -n "Health... "
curl -sf http://localhost:5080/health/ready | grep -q '"status":"Healthy"'
echo "OK"

# ... 其余检查项（Config、HTTP 遥测、503 降级等，与 V7.0 相同，略） ...

docker rm -f gw-step2 > /dev/null 2>&1 || true
echo "=== All Step 2 checks passed ==="
```

**验证**：`chmod +x scripts/verify-step2.sh`。☐

---

### 1.16 重写 Program.cs（引用 Infrastructure，DI Pipeline）

**操作**：完全替换 `IoTGateway/Program.cs`，粘贴以下内容：

```csharp
using IoTHunter.Infrastructure;
using IoTGateway.Infrastructure.Health;
using IoTGateway.Infrastructure.Messaging;
using IoTGateway.Infrastructure.Metrics;
using IoTGateway.Infrastructure.Mqtt;
using IoTGateway.Infrastructure.Options;
using IoTGateway.Infrastructure.Resilience;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) =>
    LoggingDefaults.ConfigureBaseLogger(configuration));

builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection("Kafka"));
builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<KafkaOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<MqttOptions>>().Value);

builder.Services.AddSingleton<KafkaResiliencePipeline>();
builder.Services.AddSingleton<KafkaProducerService>();
builder.Services.AddSingleton<GatewayMetrics>();
builder.Services.AddSingleton<MqttIngestionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttIngestionService>());

builder.Services.AddHealthChecks()
    .AddCheck<KafkaHealthCheck>("kafka", tags: ["external"])
    .AddCheck<MqttHealthCheck>("mqtt", tags: ["external"]);

builder.Services.Configure<HostOptions>(options =>
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

builder.Services.AddControllers();

builder.Services
    .AddIoTHunterOpenTelemetry(builder.Configuration, "IoTGateway")
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddPrometheusExporter());

var app = builder.Build();
app.UseSerilogRequestLogging();
app.UseOpenTelemetryPrometheusScrapingEndpoint();
app.MapControllers();
app.MapHealthChecks("/health/ready");
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.Run();
```

**说明**：`/health/live` 使用 `Predicate = _ => false`，仅用于进程存活与 K8s **liveness** 探针，不执行任何 `IHealthCheck`；`/health/ready` 注册 Kafka/MQTT 检查，用于 **readiness**。与架构设计文档 V7.0 健康检查策略一致。

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

## 2. Live E2E 验证剧本（V8.0 增强版）

> **前置条件**：`docker compose -f docker-compose.local.yml up -d` 已启动所有中间件。使用容器化运行网关（`docker run -d --name gw-step2 -p 5080:80 iot-gateway:step2`）。

| 编号 | 验证场景                  | 操作步骤                                                     | 预期结果                                                    | 通过 |
| ---- | ------------------------- | ------------------------------------------------------------ | ----------------------------------------------------------- | ---- |
| 2.1  | 编译                      | `dotnet build IoTGateway/IoTGateway.csproj`                  | 0 Error, 0 Warning                                          | ☐    |
| 2.2  | Docker 构建               | `docker build -t iot-gateway:step2 -f IoTGateway/Dockerfile .` | 成功构建                                                    | ☐    |
| 2.3  | 容器健康检查              | 启动容器后：`curl -s http://localhost:5080/health/ready`；再 `curl -s http://localhost:5080/health/live`（K8s liveness 语义，不跑 Kafka/MQTT 依赖检查） | `ready`：`Healthy`，kafka、mqtt 均 Healthy；`live`：`Healthy`（与 §1.16 `Predicate = _ => false` 一致） | ☐    |
| 2.4  | 配置查询端点              | `curl -s http://localhost:5080/api/v1/config`                | 含 `Mqtt.ClientId` 和 `Kafka.BootstrapServers`              | ☐    |
| 2.5  | HTTP 正常遥测             | `curl -X POST .../telemetry`                                 | HTTP 202，含 `kafkaPartition`、`kafkaOffset`                | ☐    |
| 2.6  | Kafka 可见消息            | 消费普通遥测对应 topic（默认名为 `telemetry.raw`，须与 `ReliabilityConfiguration.KafkaTopics` 中 BestEffort 键一致） | Header 含 `traceparent`、`event_id`                         | ☐    |
| 2.7  | HTTP 校验失败             | 发送缺失 deviceId 的请求                                     | HTTP 400                                                    | ☐    |
| 2.8  | Kafka 不可用返回503       | 停掉Kafka后发送请求                                          | HTTP 503，含 `"status":"unavailable"`                       | ☐    |
| 2.9  | 恢复后正常返回202         | 重启Kafka，5秒后发送请求                                     | HTTP 202                                                    | ☐    |
| 2.10 | MQTT 普通遥测             | `mosquitto_pub ... device/dev-mqtt/telemetry`                | Kafka 普通遥测 topic（默认 `telemetry.raw`，须与 `ReliabilityConfiguration.KafkaTopics` 中 BestEffort 键一致）中可见                                | ☐    |
| 2.11 | MQTT 关键事件             | `mosquitto_pub ... device/dev-mqtt/event/critical -q 1`      | Kafka 关键事件 topic（默认 `event.critical`，须与 `ReliabilityConfiguration.KafkaTopics` 中 Critical 键一致）中可见，Header `reliability_level:2` | ☐    |
| 2.12 | 自定义指标                | `curl -s http://localhost:5080/metrics \| grep gateway_`     | 至少出现三种自定义指标                                      | ☐    |
| 2.13 | 非法 MQTT Topic 拒绝      | 发布到 `device/x/bad`                                        | 网关日志警告                                                | ☐    |
| 2.14 | MQTT 断连自愈（指数退避） | 停掉 Mosquitto 10 秒后重启，观察网关日志                     | 重连尝试间隔逐渐增大，最终重连成功，HTTP 通道始终不受影响   | ☐    |

---

## 3. 人工测试操作指南

（整体结构与 V7.0 相同，但在“测试 2.14”部分强调观察指数退避的间隔变化）

### 3.1 环境准备

```bash
docker compose -f docker-compose.local.yml up -d
docker ps   # 确认 6 个容器 Running
```

### 3.2 编译与容器构建

```bash
cd IoTHunter
dotnet build IoTGateway/IoTGateway.csproj
docker build -t iot-gateway:step2 -f IoTGateway/Dockerfile .
```

**☐ 编译 0 Warning，镜像构建成功。**

### 3.3 启动容器

```bash
docker run -d --name gw-step2 -p 5080:80 iot-gateway:step2
sleep 5
docker logs gw-step2 | grep "Kafka producer initialized"
docker logs gw-step2 | grep "MQTT connected and subscribed"
```

**☐ 预期输出存在。**

### 3.4 执行基础验证

（测试 2.3 至 2.13 逐项执行，命令与预期均与 V7.0 相同）

### 3.5 MQTT 指数退避验证（2.14）

```bash
docker compose -f docker-compose.local.yml stop mosquitto
sleep 3
# 观察网关日志，应出现 "MQTT connection failed, retrying in 100ms (attempt 1)"
# 数秒后 "retrying in 200ms (attempt 2)"
docker compose -f docker-compose.local.yml start mosquitto
sleep 10
docker logs gw-step2 | grep "MQTT connected and subscribed"
```

**☐ 通过。HTTP 通道在此期间始终健康。**

### 3.6 收尾

- 保存输出至 `screenshots/step2/`。
- `docker rm -f gw-step2`。

---

## 4. 完成标准

- [ ] `dotnet build` 0 Error, 0 Warning
- [ ] Docker 镜像构建成功，容器健康检查通过（curl 已内置于镜像）
- [ ] 双协议接入：HTTP 202/503 正确，MQTT 消息正确路由至 Kafka
- [ ] Kafka Header 包含 `traceparent`、`event_id`、`schema_version`、`reliability_level`
- [ ] **Polly Pipeline 通过 DI 注入，无静态 LoggerFactory 反模式**
- [ ] **MQTT 重连采用指数退避，断连后重连成功且不丢 HTTP 能力**
- [ ] 配置查询端点 `/api/v1/config` 返回正确 JSON
- [ ] Live 验证 14 项全部勾选
- [ ] 无硬编码、无 Mock 数据
- [ ] 所有服务引用 `IoTHunter.Infrastructure`，`Shared` 库保持纯净

---

第二步开发计划 V8.0 最终版完毕。D爷，所有裁决已熔接，Pipeline 已 DI 化，MQTT 指数退避已集成，Dockerfile 已安装 curl，验证脚本已彻底跨平台。请定稿。