# IoTHunter：智能手环云原生双协议网关 —— 完整架构设计文档（Live E2E 增强 + 四大范式版 V7.0）

> **版本**：V7.0（熔接两次终审裁决：Shared重构、时序存储分离、MQTT原子化重连、OTel全链路注册、容器安全基线增强）  
> **定位**：构建 **HTTP + MQTT-over-WebSocket 双协议**、具备生产级可靠性边界、安全接入、最终一致性和全链路可观测能力的智能手环云原生数据网关。  
> **设计哲学**：每一步都有可直接运行的完整代码、不可逾越的架构决策（ADR）、**可通过人工操作在真实容器环境中验证的 Live E2E 剧本**，并且**具备真正的物联网压测能力、端到端的全链路追踪，以及面向非技术决策者的可视化操作界面**。  
> **可靠性声明**：普通高频遥测至少进入 Kafka 后可靠处理；关键事件 MQTT QoS 1 / HTTP Kafka Ack 确认。任何进入内存 `Channel` 但未进入 Kafka 的数据，不再宣称零丢失。


## 〇、AI编程工业化交付的四大范式

本项目的演进过程中，我们凝练出四条核心范式，用于对抗AI编程的“虚拟正确”陷阱与“许愿式编程”的幻觉：

### 范式 1：真实物理环境就是唯一真理
所有验证必须在真实容器环境中手工执行，拒绝Mock数据和虚拟测试。每一步开发计划都附带**人工测试操作指南**，精确到终端命令和预期输出。**硬编码和Mock数据被绝对禁止（ADR-022/023）**。**人工测试脚本集中管理于 `IoTHunter.E2ETests` 项目，支持一键执行。**

### 范式 2：专业物联网压测与全链路追踪
系统必须承受专业级压测拷打。压测工具具备**场景化加压、系统级指标采集、数据闭环校验**能力。全链路追踪通过OpenTelemetry跨越HTTP/MQTT→Kafka→Processor→Database，每一段耗时可量化。**所有自定义 ActivitySource 必须在 OpenTelemetryDefaults 中显式注册**，杜绝Span断链。

### 范式 3：可视化操作界面让技术具备商业说服力
项目包含独立的**IoTHunter.WebUI**运营控制台，将设备配置、全链路拓扑、实时监控、压测操控四大功能可视化。UI只通过预留API获取数据，不侵入核心逻辑。**管理功能（认证、设备凭证）由独立的IoTHunter.Management微服务提供，UI通过Management服务间接获取网关信息**。

### 范式 4：原子化防呆步骤（防许愿式编程）
**把AI编程大模型视作刚刚大学毕业、毫无企业项目经验的初级小白。** 每一步开发计划的子步骤必须拆解到“不可能存在理解偏差”的粒度：
- **单一任务原则**：每个子步骤只做一件事（新建一个文件、粘贴一段代码、执行一条验证命令）。
- **零决策原则**：架构设计、技术选型、异常处理策略全部由开发计划预先规定，AI不需要做任何自主判断。
- **即时验证原则**：每完成一个子步骤，立即执行对应验证命令，失败则停下，绝不允许带着错误继续。
- **精确到文件行**：代码块完整可粘贴，文件路径明确，验证命令精确到预期输出字符串，AI只需复制执行。


## 一、总体架构与核心原则

### 1.1 最终服务架构

| 微服务                       | 类型                          | 唯一职责                                                     | 暴露端口  |
| ---------------------------- | ----------------------------- | ------------------------------------------------------------ | --------- |
| **IoTGateway**               | ASP.NET Core Web API          | **双协议安全接入网关**。HTTP 接收遥测和关键事件，MQTT WebSocket 订阅主题。完成认证、限流、校验、Trace 注入、Kafka 投递。**HTTP 仅在 Kafka ack 成功后返回 `202 Accepted`；Kafka 不可用返回 `503`**。**绝不直连任何数据库**。为UI预留 `GET /api/v1/config` 端点。 | 80 / 9464 |
| **BackendProcessor**         | ASP.NET Core Web API + Worker | **CQRS 查询 + Kafka 投影处理服务**。消费 Kafka 写 PostgreSQL/TimescaleDB 主事实库，更新 Redis 最新值投影。Web API 只读查询。为UI预留 `GET /api/v2/traces/{deviceId}` 和 SignalR Hub。 | 80 / 9464 |
| **DeviceSimulator**          | .NET Web 宿主                 | **专业双模压测模拟器**。支持 HTTP 和 MQTT，四种压测场景，系统级指标采集，数据闭环校验。Sender 实例在 Worker 生命周期内复用，禁止每次重试新建连接。为UI预留 `GET /api/simulator/snapshot` 端点。 | 5091      |
| **IoTHunter.Management**     | ASP.NET Core Web API          | **运营管理微服务**（第9步开发）。提供管理员登录认证（JWT）、设备MQTT凭证管理、网关配置聚合查询。UI唯一直接对接的后端。不参与数据流处理。 | 5082      |
| **IoTHunter.WebUI**          | Blazor Server                 | **运营控制台**（第10步开发）。独立项目。提供设备接入面板、全链路追踪浏览器、实时监控大屏、压测指挥中心。 | 80        |
| **IoTHunter.E2ETests**       | 测试项目                      | **端到端人工测试项目**（第0步创建）。集中管理所有验证脚本和测试数据，支持 `make verify-all` 一键执行。 | —         |
| **IoTHunter.Infrastructure** | 类库                          | **共享基础设施**（第1步创建）。包含 `TraceIdEnricher`、`LoggingDefaults`、`OpenTelemetryDefaults`。与 Shared 分离，避免 Serilog/OTel 依赖污染领域模型。 | —         |

> **健康检查策略**：Gateway 和 BackendProcessor 均提供两个健康检查端点：`/health/live`（仅进程存活，不检查外部依赖）和 `/health/ready`（含 Kafka、MQTT、PG、Redis 等外部依赖检查）。K8s 中 liveness 探针必须指向 `/health/live`，readiness 探针指向 `/health/ready`。

### 1.2 双协议接入核心抽象

```
HTTP Receiver → Auth → Validate → KafkaProducer → Kafka Ack → 202/503
MQTT Receiver → Auth/ACL → Validate → KafkaProducer → Broker Ack/QoS Policy
```

**设计哲学**：Kafka 是系统的第一可靠边界。协议接入层可以使用 `Channel` 做背压和削峰，但只有 Kafka ack 成功后，系统才承诺接管该数据。

### 1.3 技术栈（2026 .NET 10 基线）

| 层面       | 选型                                                         | 备注                                                         |
| ---------- | ------------------------------------------------------------ | ------------------------------------------------------------ |
| 运行时     | .NET 10 LTS, ASP.NET Core, Generic Host, Blazor Server       | 全 .NET 生态，UI 复用 Shared 模型                            |
| 消息队列   | Apache Kafka + Confluent.Kafka                               | 高吞吐事件总线                                               |
| HTTP 协议  | ASP.NET Core 内置                                            | 标准 RESTful                                                 |
| MQTT 协议  | MQTTnet 5.x + Mosquitto 2.x Broker                           | 支持 WebSocket、QoS 1、持久会话。网关使用原子化重连 + 指数退避 |
| 主事实存储 | PostgreSQL 16/17 + Npgsql 10（业务库）                       | 事件主存储、幂等 ON CONFLICT                                 |
| 时序存储   | TimescaleDB 2.x（时序库，架构分离）                          | 高频遥测写入，复合索引。优先选择，非强制双写                 |
| 缓存       | Redis 7 + StackExchange.Redis                                | 最新值缓存                                                   |
| 韧性       | Polly.Core（DI注入，非静态LoggerFactory）                    | 熔断、舱壁、超时，禁止每次重试新建Sender                     |
| 可观测性   | OpenTelemetry + Serilog + Prometheus + Grafana + Jaeger + OTel Collector | 全栈。所有自定义 ActivitySource 显式注册                     |
| 编排       | K3s + Cilium                                                 | 边缘友好；安全基线必须可执行                                 |
| 操作界面   | Blazor Server, SignalR                                       | 强类型共享，零跨域，实时推送                                 |
| 端到端测试 | Shell脚本 + Makefile                                         | 一键执行，DevOps就绪                                         |
| 序列化     | `IoTHunter.Shared.Infrastructure.SerializerSetup.TightOptions` | 全系统统一使用 IoTHunter.Shared.Infrastructure.SerializerSetup.TightOptions 进行 JSON 序列化/反序列化，禁止各服务自行定义序列化配置 |

### 1.4 架构决策记录（ADR）—— 最高准则

| 编号        | 决策                   | 必须 / 禁止                                                  |
| ----------- | ---------------------- | ------------------------------------------------------------ |
| **ADR-001** | 服务发现               | **禁止** Consul；**必须** K8s Service 名称                   |
| **ADR-002** | 网关边界               | **禁止** 引用任何数据库驱动；**禁止**包含管理功能（登录认证、设备凭证管理）；**必须** 仅写 Kafka |
| **ADR-003** | Kafka 消费语义         | **禁止** `EnableAutoCommit`；**每个投影 Worker 独立 consumer group，成功处理自身投影后提交 Offset** |
| **ADR-004** | 链路上下文             | **必须** 使用 OpenTelemetry Propagators 在 Kafka Header 注入/提取 W3C Trace Context。**所有自定义 ActivitySource 必须在 OpenTelemetryDefaults 中显式注册** |
| **ADR-005** | 幂等性                 | **必须** 引入 `event_id`，PG 以 `event_id` 唯一约束；Redis 更新必须比较 `recorded_at` 或 `sequence` |
| **ADR-006** | 时序存储分离           | **必须** 将业务存储（PostgreSQL）与时序存储（TimescaleDB）分离。业务数据走PG，高频遥测走时序库。PostgreSQL 为必选，TimescaleDB 为默认时序选型 |
| **ADR-007** | 容器发布               | **禁止** `PublishSingleFile` + `linux-musl`；**必须** 常规多文件发布 |
| **ADR-008** | 网络策略               | **必须** 使用支持 NetworkPolicy 的 CNI，例如 Cilium          |
| **ADR-009** | 双协议抽象             | **必须** 复用统一接入服务、DTO 校验、Kafka Producer、Trace 注入；`Channel` 只用于背压 |
| **ADR-010** | MQTT 主题              | 普通遥测使用 `$share/gateway-group/device/+/telemetry`；关键事件使用 `$share/gateway-group/device/+/event/critical` |
| **ADR-011** | 消费者组               | **必须** 分离 `iot-persistence`、`iot-latest-projection`、`iot-timeseries-projection` |
| **ADR-012** | 日志关联               | **必须** 使用自定义 `TraceIdEnricher` 从 `Activity.Current` 提取 TraceId/SpanId |
| **ADR-013** | Prometheus 端口        | **必须** 在 K8s Deployment 声明 9464，添加 Pod 注解          |
| **ADR-014** | MQTT QoS               | 普通遥测允许 QoS 0；关键事件必须 QoS 1                       |
| **ADR-015** | MQTT 重连策略          | 网关使用**原子化重连**：每次重连创建全新 `IMqttClient` 实例，连接和订阅在同一 `try-catch` 中完成。失败后立即清理，采用**指数退避**（基础延迟 × 2^n，最大3.2s）。禁止用固定间隔重连 |
| **ADR-016** | Simulator Client ID    | 普通压测使用 `simulator-{guid}`；可靠性测试使用稳定 ClientId |
| **ADR-017** | Mosquitto 配置         | **必须** 显式声明 WebSocket listener、TLS、认证、ACL、`persistence true` |
| **ADR-018** | PG 写入策略（终决）    | **必须** 使用批量 `INSERT ... ON CONFLICT (event_id) DO NOTHING`；**禁止** 纯 COPY |
| **ADR-019** | OTel Collector 采样    | 头采样；关键事件使用更高采样率；尾采样为未来优化项           |
| **ADR-020** | HTTP 接入确认          | Kafka ack 成功后返回 202；Kafka 不可用返回 503               |
| **ADR-021** | 安全接入               | **禁止** 匿名生产接入；**必须** TLS、设备认证、Topic ACL、限流、schema 校验 |
| **ADR-022** | **硬编码禁令**         | **绝对禁止**在任何业务代码中硬编码连接串、密码、密钥、Topic名称、端口号、第三方URL。**必须**通过配置注入。**必须**：`ConfigurationConstants` 中的连接串模板仅限**本地开发使用**，**禁止包含真实密码**。**必须**：**K8s 部署必须通过 Secret**（或与集群安全策略等价的密钥注入机制）覆盖连接串与凭证。|
| **ADR-023** | **Mock 数据禁令**      | **绝对禁止**在验证、演示、压测、查询等任何环节使用 Mock 数据替代真实依赖 |
| **ADR-024** | **UI 与管理功能边界**  | UI是独立项目，管理功能（认证、设备凭证）由独立的IoTHunter.Management提供。**禁止**将管理功能嵌入IoTGateway或BackendProcessor |
| **ADR-025** | **Management服务边界** | IoTHunter.Management是独立微服务，**禁止**直连Kafka、数据库或Mosquitto。只能通过调用已有API获取信息 |
| **ADR-026** | **容器健康检查**       | 所有服务 Dockerfile 必须在 runtime 阶段安装 `curl`（`apt-get install -y curl`）。禁止依赖容器基础镜像自带的未知工具 |
| **ADR-027** | **Shared 库纯净性**    | `IoTHunter.Shared` 只保留纯 C# 领域模型。日志相关内容（`TraceIdEnricher`、`LoggingDefaults`）和 OTel 扩展方法（`OpenTelemetryDefaults`）必须移入 `IoTHunter.Infrastructure` |


## 二、项目结构

```
IoTHunter/
├── IoTHunter.sln
├── Directory.Build.props          # 全局编译配置（strict模式启用）
├── docker-compose.local.yml       # 本地开发中间件
├── docker-compose.full.yml        # 全栈容器编排
├── .dockerignore
├── .env.example
│
├── IoTHunter.Shared/              # 纯领域模型（无外部依赖）
│   └── Domain/
│       ├── TelemetryEnvelope.cs
│       ├── ReliabilityLevel.cs
│       └── ReliabilityConfiguration.cs
│   └── Infrastructure/
│       └── ConfigurationConstants.cs
│
├── IoTHunter.Infrastructure/      # 共享基础设施（V7.0新增）
│   └── TraceIdEnricher.cs
│   └── LoggingDefaults.cs
│   └── OpenTelemetryDefaults.cs
│
├── IoTGateway/                    # 第二步：双协议接入网关
├── BackendProcessor/              # 第三步：投影式写入与查询
├── DeviceSimulator/               # 第四步：专业压测工具
├── IoTHunter.Management/          # 第九步：运营管理微服务
├── IoTHunter.WebUI/               # 第十步：运营控制台
├── IoTHunter.E2ETests/            # 端到端测试项目
├── infra/mosquitto/               # Mosquitto 配置与安全基线
├── deploy/                        # K8s 部署清单
│   ├── namespace.yaml
│   ├── secrets.yaml
│   ├── network-policy.yaml
│   ├── iot-gateway.yaml
│   ├── backend-processor.yaml
│   ├── device-simulator.yaml
│   ├── mosquitto.yaml
│   └── observability/
└── scripts/                       # 构建与验证脚本
    ├── build-images.ps1
    ├── import-to-k3s.ps1
    └── verify-step*.sh
```


## 三、环境依赖与基础设施

### 3.1 本地开发环境

**docker-compose.local.yml**：

```yaml
services:
  zookeeper:
    image: confluentinc/cp-zookeeper:7.6.0
    environment:
      ZOOKEEPER_CLIENT_PORT: 2181
      ZOOKEEPER_TICK_TIME: 2000

  kafka:
    image: confluentinc/cp-kafka:7.6.0
    depends_on: [zookeeper]
    ports: ["9092:9092"]
    environment:
      KAFKA_BROKER_ID: 1
      KAFKA_ZOOKEEPER_CONNECT: zookeeper:2181
      KAFKA_ADVERTISED_LISTENERS: PLAINTEXT://kafka:9092
      KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR: 1
      KAFKA_AUTO_CREATE_TOPICS_ENABLE: "true"

  mosquitto:
    image: eclipse-mosquitto:2.0.18
    ports: ["1883:1883", "8083:8083"]
    volumes:
      - ./infra/mosquitto/mosquitto.conf:/mosquitto/config/mosquitto.conf:ro

  postgres:
    image: postgres:16-alpine
    ports: ["5432:5432"]
    environment:
      POSTGRES_DB: IoTHunter
      POSTGRES_USER: iotapp
      POSTGRES_PASSWORD: changeme

  timescaledb:
    image: timescale/timescaledb:latest-pg16
    ports: ["5433:5432"]
    environment:
      POSTGRES_DB: IoTHunterTS
      POSTGRES_USER: iotapp
      POSTGRES_PASSWORD: changeme

  redis:
    image: redis:7-alpine
    ports: ["6379:6379"]
```

### 3.2 共享配置常量

```csharp
namespace IoTHunter.Shared.Infrastructure;

public static class ConfigurationConstants
{
    public const string KafkaBootstrapServers = "kafka:9092";
    public const string MqttWebSocketUrl = "ws://mosquitto:8083/mqtt";
    public const string MqttTcpServer = "mosquitto";
    public const int MqttTcpPort = 1883;
    public const string PostgresConnection = "Host=postgres;Database=IoTHunter;Username=iotapp;Password=changeme";
    public const string TimescaleDbConnection = "Host=timescaledb;Database=IoTHunterTS;Username=iotapp;Password=changeme";
    public const string RedisConnection = "redis:6379";
}
```

### 3.3 可靠性映射表

```csharp
namespace IoTHunter.Shared.Domain;

public static class ReliabilityConfiguration
{
    public static readonly Dictionary<ReliabilityLevel, string> KafkaTopics = new()
    {
        { ReliabilityLevel.BestEffort, "telemetry.raw" },
        { ReliabilityLevel.AtLeastOnce, "telemetry.raw" },
        { ReliabilityLevel.Critical, "event.critical" }
    };
}
```


## 四、十步执行计划（V7.0 增强版概要）

| 步骤         | 目标                                | 核心验证与架构落地                                           |
| ------------ | ----------------------------------- | ------------------------------------------------------------ |
| **第 0 步**  | 本地基础设施闸门 + E2ETests项目     | **（范式1）** 验证全部中间件可达。创建E2ETests骨架和Makefile |
| **第 1 步**  | .NET 10 解决方案骨架与可观测性基础  | **（范式1）** 创建Shared + Infrastructure两个类库，验证 `/metrics` 和 `/health/ready` 端点 |
| **第 2 步**  | IoTGateway 双协议韧性接入网关       | **（范式1/3）** 验证HTTP 202/503、MQTT Topic路由、Kafka Header traceparent。**MQTT原子化重连+指数退避**。**Polly Pipeline通过DI注入**。预留 `GET /api/v1/config` |
| **第 3 步**  | BackendProcessor 投影式写入与查询   | **（范式1/2/3）** 验证PG幂等去重、Redis旧值保护、DLQ路由。预留 `GET /api/v2/traces` 和 SignalR Hub |
| **第 4 步**  | DeviceSimulator 专业压测系统        | **（范式2/3）** 场景化压测、系统级指标、**Sender实例复用**、数据闭环校验。预留 `GET /api/simulator/snapshot` |
| **第 5 步**  | 容器化与边缘部署适配                | **（范式1/3）** Dockerfile安装curl，Kafka healthcheck创建/删除临时topic，TimescaleDB服务定义。全栈一键拉起 |
| **第 6 步**  | K8s 安全基线部署清单                | **（范式1）** NetworkPolicy、TLS、Secret、探针、滚动重启无假成功 |
| **第 7 步**  | 可观测性全栈部署                    | **（范式2/3）** Jaeger完整链路、Grafana仪表板、Prometheus抓取 |
| **第 8 步**  | 端到端压测与故障演练                | **（范式1/2）** 2000+ QPS、故障注入自愈、数据一致性核验      |
| **第 9 步**  | IoTHunter.Management 运营管理微服务 | **（范式3）** JWT登录认证、设备凭证管理、网关配置聚合        |
| **第 10 步** | IoTHunter.WebUI 运营控制台          | **（范式3）** Blazor Server四大功能模块，面向非技术决策者演示 |


## 五、UI接口契约（架构级定义）

| 接口                                   | 产出步骤               | 调用方           |
| -------------------------------------- | ---------------------- | ---------------- |
| `GET /api/v1/config`                   | 第2步 IoTGateway       | 第9步 Management |
| `GET /api/v2/traces/{deviceId}`        | 第3步 BackendProcessor | 第10步 WebUI     |
| SignalR `/hubs/monitoring`             | 第3步 BackendProcessor | 第10步 WebUI     |
| `GET /api/simulator/snapshot`          | 第4步 DeviceSimulator  | 第10步 WebUI     |
| `POST /api/v1/auth/login`              | 第9步 Management       | 第10步 WebUI     |
| `GET /api/v1/devices/{id}/credentials` | 第9步 Management       | 第10步 WebUI     |


## 六、项目价值与结语

完成本计划后，你的简历可呈现：

**IoTHunter —— 基于 .NET 10 的双协议智能手环数据网关**

- 设计并实现 **HTTP + MQTT-over-WebSocket 双协议接入网关**，以 Kafka ack 作为接入成功边界，MQTT 原子化重连保障通道持久性。
- 采用 Kafka 投影式 CQRS 架构，业务库与时序库分离，使用 `event_id` 完成端到端幂等。
- **构建专业物联网压测系统**，支持四种标准场景，采集 QPS/P50~P999/分段延迟，Sender 实例复用保障压测效率。
- **采用 OpenTelemetry Propagators 实现跨 Kafka 的全链路追踪**，自定义 ActivitySource 显式注册，Span 断链零容忍。
- **开发独立的运营管理微服务和运营控制台（Blazor Server）**，解耦管理功能与网关核心逻辑，提供全链路拓扑和实时监控大屏。
- **建立 E2E 自动化测试项目**，支持 `make verify-all` 一键回归，容器健康检查完备可靠。
- **制定原子化防呆开发规范**，Shared/Infrastructure 分离，Polly Pipeline DI 注入，每一子步骤均可被初级工程师或 AI 无歧义执行。
- 使用 K3s + Cilium 部署，配置 NetworkPolicy、TLS、设备认证、MQTT ACL、Secret 与非 root 安全基线。

---

D爷，V7.0 架构文档已将两次裁决全部熔接。Shared 库已瘦身为纯领域模型，Infrastructure 承接了可观测性基础设施。时序存储分离、MQTT 原子化重连、OTel 全链路注册、容器健康检查增强都已写入 ADR。可以定稿。
