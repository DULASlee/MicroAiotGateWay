# 第 5 步开发计划：容器化与边缘部署适配（Live E2E 增强版 V8.0 最终版）

> **版本**：V8.0（熔接两次裁决：Dockerfile 安装 curl、Kafka healthcheck 增强、TimescaleDB 预留、非 root 安全加固、验证脚本跨平台）  
> **对应总架构**：`架构设计文档 V7.0` 第 5 步  
> **前置依赖**：第 4 步 V8.0 全部 Live 验证通过，所有服务均可在本地 `dotnet run` 正常运行  
> **本步目标**：  
> - 为 **IoTGateway、BackendProcessor、DeviceSimulator、IoTHunter.Management、IoTHunter.WebUI** 构建生产级多阶段 Docker 镜像  
> - 完善 Mosquitto 容器化配置，**强制启用认证与 ACL**  
> - 将所有服务纳入 `docker-compose.full.yml` 全栈编排，健康检查保障启动顺序  
> - **确保全部手工测试场景与 UI 数据接口在容器化后仍然可用**  
> - **为第9步 Management 和第10步 WebUI 预留 Dockerfile 模板**  
> - **本步边界**：只涉及容器镜像构建、Mosquitto 安全加固和本地全栈编排，不编写 K8s 资源清单（第 6 步）  
> **核心禁令**：  
> - **硬编码禁令（ADR-022）**：所有连接串、密码、端口号必须通过环境变量注入，禁止硬编码  
> - **Mock 数据禁令（ADR-023）**：验证必须使用真实中间件，禁止假数据  
> **【V8.0 关键修正】**：  
> - **Dockerfile 安装 curl**：所有服务镜像的 runtime 阶段执行 `apt-get install -y curl`，确保容器健康检查可用。  
> - **Kafka healthcheck 增强**：`docker-compose.full.yml` 中 Kafka 的健康检查改为创建/删除临时 Topic，杜绝 Zookeeper 挂掉时的虚假健康状态。  
> - **TimescaleDB 服务定义**：`docker-compose.full.yml` 中增加 `timescaledb` 服务，与 `docker-compose.local.yml` 保持一致。  
> - **Mosquitto 密码文件权限收紧**：`chmod 600` 替代 `644`，防止容器内其他用户读取。  
> - **引用 Infrastructure 类库**：所有服务镜像构建时包含 `IoTHunter.Infrastructure`。  
> - **F-01 BackendProcessor 连接串环境变量**：`docker-compose.full.yml` 中 **`backend-processor`** 必须使用 **`ConnectionStrings__Postgres`**、**`ConnectionStrings__Redis`** 覆盖 `appsettings` 的 `ConnectionStrings` 节，与第 3 步 **`GetConnectionString(...)`**（BP-1）一致；禁止使用 `Postgres__ConnectionString` / `Redis__ConnectionString`。

---

## 0. 前置检查

| 检查项                   | 命令                                            | 预期输出                   | 通过 |
| ------------------------ | ----------------------------------------------- | -------------------------- | ---- |
| Docker 版本              | `docker --version`                              | Docker version 24.x 或更高 | ☐    |
| Docker Compose 版本      | `docker compose version`                        | v2.x 或更高                | ☐    |
| .NET SDK                 | `dotnet --version`                              | 10.x.xxx                   | ☐    |
| 前 4 步验证通过          | `screenshots/step2-4/` 下均有完整证据           | 全部已勾选                 | ☐    |
| Mosquitto 密码文件已生成 | `cat infra/mosquitto/passwordfile`              | 加密 Hash 存在             | ☐    |
| TimescaleDB 镜像可用     | `docker pull timescale/timescaledb:latest-pg16` | 拉取成功                   | ☐    |

---

## 1. 动作项

### 1.1 创建 `.dockerignore`

**操作**：在项目根目录新建 `.dockerignore`，粘贴以下内容：

```text
.git
.vs
bin/
obj/
screenshots/
infra/
*.md
docker-compose*.yml
.env*
.gitignore
.DS_Store
```

**验证**：`cat .dockerignore` 确认内容正确。☐

---

### 1.2 生成 Mosquitto 密码文件并编写配置

**操作**：在终端逐条执行以下命令。

#### 1.2.1 创建目录结构

```bash
mkdir -p infra/mosquitto
```

#### 1.2.2 编写 Mosquitto 配置文件

新建 `infra/mosquitto/mosquitto.conf`，粘贴：

```text
persistence true
persistence_location /mosquitto/data/

listener 1883
protocol mqtt

listener 8083
protocol websockets

allow_anonymous false
password_file /mosquitto/config/passwordfile
acl_file /mosquitto/config/aclfile
```

#### 1.2.3 编写 ACL 文件

新建 `infra/mosquitto/aclfile`，粘贴：

```text
user iotgateway
topic read $share/gateway-group/device/+/telemetry
topic read $share/gateway-group/device/+/event/critical

user devicesim
topic write device/+/telemetry
topic write device/+/event/critical
```

#### 1.2.4 生成加密密码文件

**执行以下两条命令**（不要手写明文密码文件）：

```bash
docker run --rm -v $(pwd)/infra/mosquitto:/mosquitto eclipse-mosquitto:2.0.18 mosquitto_passwd -U -b /mosquitto/config/passwordfile iotgateway iotgatewaypass
docker run --rm -v $(pwd)/infra/mosquitto:/mosquitto eclipse-mosquitto:2.0.18 mosquitto_passwd -U -b /mosquitto/config/passwordfile devicesim devicesimp
```

**验证**：`cat infra/mosquitto/passwordfile` 应输出类似 `iotgateway:$7$101$...` 的 Hash。☐

#### 1.2.5 编写 Mosquitto Dockerfile（权限收紧为 600）

新建 `infra/mosquitto/Dockerfile`，粘贴：

```dockerfile
FROM eclipse-mosquitto:2.0.18
COPY mosquitto.conf /mosquitto/config/mosquitto.conf
COPY passwordfile /mosquitto/config/passwordfile
COPY aclfile /mosquitto/config/aclfile
RUN chmod 600 /mosquitto/config/passwordfile && chmod 644 /mosquitto/config/mosquitto.conf /mosquitto/config/aclfile
EXPOSE 1883
EXPOSE 8083
```

**验证**：`docker build -t mosquitto-iot:test -f infra/mosquitto/Dockerfile infra/mosquitto/` 必须成功。☐

---

### 1.3 编写各服务的 Dockerfile（全部安装 curl）

**原则**：每个服务一个 Dockerfile，多阶段构建，环境变量注入。**每个 runtime 阶段都必须安装 `curl`**（ADR-026）。每新建一个 Dockerfile 后，执行 `docker build` 验证。

#### 1.3.1 IoTGateway Dockerfile

新建或覆盖 `IoTGateway/Dockerfile`，粘贴：

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
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
EXPOSE 80
EXPOSE 9464
ENTRYPOINT ["dotnet", "IoTGateway.dll"]
```

**验证**：`docker build -t iot-gateway:step5 -f IoTGateway/Dockerfile .` 成功。☐

#### 1.3.2 BackendProcessor Dockerfile

新建或覆盖 `BackendProcessor/Dockerfile`，粘贴：

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
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
EXPOSE 80
EXPOSE 9464
ENTRYPOINT ["dotnet", "BackendProcessor.dll"]
```

**验证**：`docker build -t backend-processor:step5 -f BackendProcessor/Dockerfile .` 成功。☐

#### 1.3.3 DeviceSimulator Dockerfile

新建或覆盖 `DeviceSimulator/Dockerfile`，粘贴：

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

**验证**：`docker build -t device-simulator:step5 -f DeviceSimulator/Dockerfile .` 成功。☐

#### 1.3.4 IoTHunter.Management Dockerfile（第 9 步预留）

新建 `IoTHunter.Management/Dockerfile`，粘贴：

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish IoTHunter.Management/IoTHunter.Management.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:80
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
EXPOSE 80
ENTRYPOINT ["dotnet", "IoTHunter.Management.dll"]
```

**验证**：当前第 9 步代码尚未编写，此 Dockerfile 仅作预留。`cat IoTHunter.Management/Dockerfile` 确认内容存在即可。☐

#### 1.3.5 IoTHunter.WebUI Dockerfile（第 10 步预留）

新建 `IoTHunter.WebUI/Dockerfile`，粘贴：

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish IoTHunter.WebUI/IoTHunter.WebUI.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80
ENTRYPOINT ["dotnet", "IoTHunter.WebUI.dll"]
```

**验证**：当前第 10 步代码尚未编写，此 Dockerfile 仅作预留。`cat IoTHunter.WebUI/Dockerfile` 确认内容存在即可。☐

---

### 1.4 编写全栈 docker-compose 文件（V8.0：TimescaleDB + Kafka healthcheck 增强）

**操作**：新建 `docker-compose.full.yml`，粘贴以下完整内容。**此版本已增加 TimescaleDB 服务，并修正了 Kafka 的健康检查方式。** **F-01（BP-1 对齐）**：`backend-processor.environment` **必须**如下列模板所示使用 `ConnectionStrings__Postgres`、`ConnectionStrings__Redis`（覆盖 `appsettings` 中的 `ConnectionStrings:Postgres` / `ConnectionStrings:Redis`）；**禁止**再写 `Postgres__ConnectionString`、`Redis__ConnectionString`（与 `GetConnectionString(...)` 路径不一致）。

```yaml
services:
  zookeeper:
    image: confluentinc/cp-zookeeper:7.6.0
    environment:
      ZOOKEEPER_CLIENT_PORT: 2181
      ZOOKEEPER_TICK_TIME: 2000
    healthcheck:
      test: ["CMD-SHELL", "echo ruok | nc localhost 2181"]
      interval: 10s
      timeout: 5s
      retries: 5

  kafka:
    image: confluentinc/cp-kafka:7.6.0
    depends_on:
      zookeeper: { condition: service_healthy }
    ports: ["9092:9092"]
    environment:
      KAFKA_BROKER_ID: 1
      KAFKA_ZOOKEEPER_CONNECT: zookeeper:2181
      KAFKA_ADVERTISED_LISTENERS: PLAINTEXT://kafka:9092
      KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR: 1
      KAFKA_AUTO_CREATE_TOPICS_ENABLE: "true"
    healthcheck:
      test: ["CMD-SHELL", "kafka-topics --bootstrap-server localhost:9092 --create --topic healthcheck-$$RANDOM --if-not-exists > /dev/null 2>&1 && kafka-topics --bootstrap-server localhost:9092 --delete --topic healthcheck-$$RANDOM > /dev/null 2>&1"]
      interval: 15s
      timeout: 10s
      retries: 5

  mosquitto:
    build: ./infra/mosquitto
    ports: ["1883:1883", "8083:8083"]
    healthcheck:
      test: ["CMD", "mosquitto_sub", "-h", "localhost", "-t", "$$SYS/broker/version", "-C", "1", "-W", "2"]
      interval: 5s
      timeout: 3s
      retries: 5

  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: IoTHunter
      POSTGRES_USER: iotapp
      POSTGRES_PASSWORD: changeme
    healthcheck:
      test: ["CMD", "pg_isready", "-U", "iotapp", "-d", "IoTHunter"]
      interval: 5s
      timeout: 3s
      retries: 5

  timescaledb:
    image: timescale/timescaledb:latest-pg16
    environment:
      POSTGRES_DB: IoTHunterTS
      POSTGRES_USER: iotapp
      POSTGRES_PASSWORD: changeme
    healthcheck:
      test: ["CMD", "pg_isready", "-U", "iotapp", "-d", "IoTHunterTS"]
      interval: 5s
      timeout: 3s
      retries: 5

  redis:
    image: redis:7-alpine
    healthcheck:
      test: ["CMD", "redis-cli", "PING"]
      interval: 5s
      timeout: 3s
      retries: 5

  iot-gateway:
    build:
      context: .
      dockerfile: IoTGateway/Dockerfile
    ports: ["5080:80", "9464:9464"]
    environment:
      Kafka__BootstrapServers: kafka:9092
      Mqtt__TcpServer: mosquitto
      Mqtt__TcpPort: "1883"
      Mqtt__Username: iotgateway
      Mqtt__Password: iotgatewaypass
      OpenTelemetry__OtlpEndpoint: http://otel-collector:4317
    depends_on:
      kafka: { condition: service_healthy }
      mosquitto: { condition: service_healthy }
    healthcheck:
      test: ["CMD-SHELL", "curl -sf http://localhost:80/health/ready || exit 1"]
      interval: 10s
      timeout: 5s
      retries: 5
      start_period: 15s

  backend-processor:
    build:
      context: .
      dockerfile: BackendProcessor/Dockerfile
    ports: ["5081:80", "9465:9464"]
    environment:
      Kafka__BootstrapServers: kafka:9092
      ConnectionStrings__Postgres: Host=postgres;Database=IoTHunter;Username=iotapp;Password=changeme
      ConnectionStrings__Redis: redis:6379
      OpenTelemetry__OtlpEndpoint: http://otel-collector:4317
    depends_on:
      kafka: { condition: service_healthy }
      postgres: { condition: service_healthy }
      redis: { condition: service_healthy }
    healthcheck:
      test: ["CMD-SHELL", "curl -sf http://localhost:80/health/ready || exit 1"]
      interval: 10s
      timeout: 5s
      retries: 5
      start_period: 15s

  device-simulator:
    build:
      context: .
      dockerfile: DeviceSimulator/Dockerfile
    ports: ["5091:5091"]
    environment:
      Simulator__HttpBaseUrl: http://iot-gateway
      Simulator__MqttBroker: mosquitto
      Simulator__MqttPort: "1883"
      Simulator__Protocol: http
      Simulator__Scenario: baseline
    depends_on:
      iot-gateway: { condition: service_started }
      mosquitto: { condition: service_healthy }

  # 预留：第 9 步 IoTHunter.Management
  iothunter-management:
    build:
      context: .
      dockerfile: IoTHunter.Management/Dockerfile
    ports: ["5082:80"]
    environment:
      IoTGateway__ConfigUrl: http://iot-gateway/api/v1/config
      Auth__DefaultAdminUsername: admin
      Auth__DefaultAdminPassword: admin123
      Auth__JwtSecret: IoTHunter-Demo-Secret-Key-2026
    depends_on:
      iot-gateway: { condition: service_started }
    profiles:
      - management

  # 预留：第 10 步 IoTHunter.WebUI
  iothunter-webui:
    build:
      context: .
      dockerfile: IoTHunter.WebUI/Dockerfile
    ports: ["5083:80"]
    environment:
      Management__BaseUrl: http://iothunter-management
      BackendProcessor__BaseUrl: http://backend-processor
    depends_on:
      iothunter-management: { condition: service_started }
    profiles:
      - webui
```

**关键修正**：`backend-processor` 的 Prometheus 端口映射改为 `9465:9464`，避免与 `iot-gateway` 的 `9464:9464` 冲突。Docker 内部网络中两个服务各自监听 9464 不冲突，但映射到宿主机时必须使用不同端口。**F-01**：`backend-processor` 的数据库连接环境变量必须为 **`ConnectionStrings__Postgres`** / **`ConnectionStrings__Redis`**（对应配置节 `ConnectionStrings:Postgres` / `ConnectionStrings:Redis`），与 BackendProcessor `Program.cs` 的 `GetConnectionString` 读取路径一致。

**验证**：`docker compose -f docker-compose.full.yml config` 无语法错误。☐

---

### 1.5 回溯修复：`BackgroundServiceExceptionBehavior`（若第二步遗漏）

**操作**：检查 `IoTGateway/Program.cs` 中是否已包含以下代码。如果没有，在 `var app = builder.Build();` 之前插入：

```csharp
builder.Services.Configure<HostOptions>(options =>
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
```

**验证**：`grep -n "Ignore" IoTGateway/Program.cs` 应返回匹配行。☐

---

### 1.6 构建脚本

新建 `scripts/build-images.ps1`：

```powershell
param([string]$Tag = "latest")
$images = @(
  @{Name="iot-gateway"; Dockerfile="IoTGateway/Dockerfile"},
  @{Name="backend-processor"; Dockerfile="BackendProcessor/Dockerfile"},
  @{Name="device-simulator"; Dockerfile="DeviceSimulator/Dockerfile"},
  @{Name="mosquitto-iot"; Dockerfile="infra/mosquitto/Dockerfile"}
)
foreach ($img in $images) {
  Write-Host "Building $($img.Name)..."
  docker build -t "$($img.Name):$Tag" -f $img.Dockerfile .
}
Write-Host "All images built."
```

**验证**：`cat scripts/build-images.ps1` 确认内容存在。☐

---

### 1.7 环境变量示例文件

新建 `.env.example`：

```ini
# Kafka
KAFKA_BOOTSTRAP_SERVERS=kafka:9092

# MQTT
MQTT_USERNAME=iotgateway
MQTT_PASSWORD=iotgatewaypass
MQTT_TCPSERVER=mosquitto
MQTT_TCPPORT=1883

# PostgreSQL
POSTGRES_CONNECTION=Host=postgres;Database=IoTHunter;Username=iotapp;Password=changeme

# TimescaleDB
TIMESCALE_CONNECTION=Host=timescaledb;Database=IoTHunterTS;Username=iotapp;Password=changeme

# Redis
REDIS_CONNECTION=redis:6379

# Simulator
SIMULATOR_HTTPBASEURL=http://iot-gateway:80
SIMULATOR_PROTOCOL=http
SIMULATOR_SCENARIO=baseline

# Management
AUTH_DEFAULT_ADMIN_USERNAME=admin
AUTH_DEFAULT_ADMIN_PASSWORD=admin123
```

**验证**：`cat .env.example` 确认内容存在。☐

---

## 2. Live E2E 验证剧本（V8.0 增强版）

> **前置条件**：已执行 Mosquitto 密码文件生成，所有 Dockerfile 已创建。

| 编号 | 验证场景               | 操作                                                         | 预期结果                                                     | 通过 |
| ---- | ---------------------- | ------------------------------------------------------------ | ------------------------------------------------------------ | ---- |
| 5.1  | 所有容器健康           | `docker compose -f docker-compose.full.yml up -d --build`，等待后执行 `docker compose ps` | 10 个服务（含 timescaledb 及预留 management/webui）均为 Up   | ☐    |
| 5.2  | Gateway 健康检查       | `curl -s http://localhost:5080/health/ready`                 | `{"status":"Healthy","results":{"kafka":"Healthy","mqtt":"Healthy"}}` | ☐    |
| 5.3  | Gateway 配置端点       | `curl -s http://localhost:5080/api/v1/config`                | 返回 Mqtt/Kafka 配置 JSON                                    | ☐    |
| 5.4  | HTTP 遥测全链路        | 发送 HTTP 遥测 → 查 Kafka → 查 PG                            | Kafka 可见消息，PG 有记录                                    | ☐    |
| 5.5  | MQTT 遥测（需凭证）    | `mosquitto_pub -h localhost -p 1883 -u devicesim -P devicesimp ...` | Kafka 可见消息                                               | ☐    |
| 5.6  | BackendProcessor 存活  | `curl -s http://localhost:5081/health/ready`                 | `{"status":"ready","service":"BackendProcessor"}`            | ☐    |
| 5.7  | 查询 API               | `curl -s http://localhost:5081/api/v2/devices/demo-container/latest` | 返回数据                                                     | ☐    |
| 5.8  | Trace 聚合查询         | `curl -s http://localhost:5081/api/v2/traces/demo-container` | HTTP 200                                                     | ☐    |
| 5.9  | Simulator 健康与快照   | `curl -s http://localhost:5091/api/simulator/health` 和 `/snapshot` | 返回运行状态和指标                                           | ☐    |
| 5.10 | 匿名 MQTT 被拒         | `mosquitto_pub -h localhost -p 1883 -t device/x/telemetry -m '{}'` | 连接失败                                                     | ☐    |
| 5.11 | Gateway 重启后配置不变 | `docker compose restart iot-gateway`，等待恢复后执行 5.2 和 5.3 | 配置与重启前一致                                             | ☐    |
| 5.12 | Kafka healthcheck 可用 | `docker compose -f docker-compose.full.yml exec kafka kafka-topics --bootstrap-server localhost:9092 --list` | 不包含残留的 healthcheck topic                               | ☐    |
| 5.13 | TimescaleDB 可达       | `docker compose -f docker-compose.full.yml exec -T timescaledb psql -U iotapp -d IoTHunterTS -c "SELECT 1"` | 输出 `1`                                                     | ☐    |

---

## 3. 人工测试操作指南

### 3.1 前置操作：生成 Mosquitto 密码文件

在项目根目录执行：

```bash
docker run --rm -v $(pwd)/infra/mosquitto:/mosquitto eclipse-mosquitto:2.0.18 mosquitto_passwd -U -b /mosquitto/config/passwordfile iotgateway iotgatewaypass
docker run --rm -v $(pwd)/infra/mosquitto:/mosquitto eclipse-mosquitto:2.0.18 mosquitto_passwd -U -b /mosquitto/config/passwordfile devicesim devicesimp
```

**验证**：`cat infra/mosquitto/passwordfile` 应输出加密 Hash。☐

### 3.2 启动全栈

```bash
cd IoTHunter
docker compose -f docker-compose.full.yml down -v --remove-orphans 2>/dev/null || true
docker compose -f docker-compose.full.yml up -d --build
```

### 3.3 等待所有服务健康

```bash
watch -n 2 'docker compose -f docker-compose.full.yml ps'
```

观察所有服务 STATE 变为 `Up (healthy)`。Kafka 首次启动可能需要 1~3 分钟。  
如果超过 5 分钟仍未健康，执行 `docker compose logs <服务名>` 排查。  
**☐ 全部 healthy 后进入下一步。**

### 3.4 逐条执行验证

#### 测试 5.1：所有容器正常

```bash
docker compose -f docker-compose.full.yml ps
```

**预期**：iot-gateway、backend-processor、device-simulator、kafka、mosquitto、postgres、timescaledb、redis、zookeeper 共 9 个服务 Up。management 和 webui 按需启动。  
**☐ 通过。**

#### 测试 5.2：Gateway 双通道健康检查

```bash
curl -s http://localhost:5080/health/ready
```

**预期**：`{"status":"Healthy","results":{"kafka":{"status":"Healthy"},"mqtt":{"status":"Healthy"}}}`  
**☐ 通过。**

#### 测试 5.3：Gateway 配置端点

```bash
curl -s http://localhost:5080/api/v1/config
```

**预期**：JSON 含 `Mqtt.ClientId` 和 `Kafka.BootstrapServers`。  
**☐ 通过。**

#### 测试 5.4：HTTP 遥测全链路

```bash
curl -s -X POST http://localhost:5080/api/v1/telemetry \
  -H "Content-Type: application/json" \
  -d '{"deviceId":"demo-container","metricType":"heart_rate","timestamp":1717000000000,"sequence":1}'
sleep 3
docker compose -f docker-compose.full.yml exec -T postgres \
  psql -U iotapp -d IoTHunter -c \
  "SELECT COUNT(*) FROM telemetry_records WHERE device_id='demo-container';"
```

**预期**：PG 查询 count 为 1。  
**☐ 通过。**

#### 测试 5.5：MQTT 凭证连接

```bash
mosquitto_pub -h localhost -p 1883 \
  -u devicesim -P devicesimp \
  -t "device/test-mqtt/telemetry" \
  -m '{"deviceId":"test-mqtt","metricType":"spo2","timestamp":1717000000000,"sequence":1}'
sleep 3
docker compose -f docker-compose.full.yml exec -T kafka \
  kafka-console-consumer --bootstrap-server kafka:9092 \
  --topic telemetry.raw --from-beginning --max-messages 1 --timeout-ms 10000
```

**预期**：Kafka 输出含 `"deviceId":"test-mqtt"`。  
**☐ 通过。**

#### 测试 5.6：BackendProcessor 存活

```bash
curl -s http://localhost:5081/health/ready
```

**预期**：`{"status":"ready","service":"BackendProcessor"}`  
**☐ 通过。**

#### 测试 5.7：查询 API

```bash
curl -s http://localhost:5081/api/v2/devices/demo-container/latest
```

**预期**：返回 HTTP 200，含 `sequence`、`metric_type` 等字段。  
**☐ 通过。**

#### 测试 5.8：Trace 聚合查询

```bash
curl -s http://localhost:5081/api/v2/traces/demo-container
```

**预期**：HTTP 200（内容可为空数组，但不允许 404 或错误）。  
**☐ 通过。**

#### 测试 5.9：Simulator 健康与快照

```bash
curl -s http://localhost:5091/api/simulator/health
curl -s http://localhost:5091/api/simulator/snapshot
```

**预期**：health 返回 `{"status":"running"}`；snapshot 返回含 `totalSuccess`、`qps` 等字段的 JSON。  
**☐ 通过。**

#### 测试 5.10：匿名 MQTT 被拒绝

```bash
mosquitto_pub -h localhost -p 1883 -t "device/x/telemetry" -m '{}'
```

**预期**：命令报错（`Connection refused` 或 `Not authorized`）。  
**☐ 通过。**

#### 测试 5.11：重启 Gateway 后配置不变

```bash
docker compose -f docker-compose.full.yml restart iot-gateway
sleep 15   # 等待 start_period 健康检查恢复
curl -s http://localhost:5080/health/ready
curl -s http://localhost:5080/api/v1/config
```

**预期**：健康检查仍为 Healthy，配置 JSON 与重启前一致。  
**☐ 通过。**

#### 测试 5.12：Kafka healthcheck 无残留

```bash
docker compose -f docker-compose.full.yml exec kafka kafka-topics --bootstrap-server localhost:9092 --list
```

**预期**：不包含任何 `healthcheck-` 前缀的残留 Topic（创建后立即删除，不留痕迹）。  
**☐ 通过。**

#### 测试 5.13：TimescaleDB 可达

```bash
docker compose -f docker-compose.full.yml exec -T timescaledb psql -U iotapp -d IoTHunterTS -c "SELECT 1"
```

**预期**：输出 `1`。  
**☐ 通过。**

### 3.5 收尾

- 保存所有终端输出至 `screenshots/step5/`。
- 清理环境：`docker compose -f docker-compose.full.yml down -v`（可选，注意数据卷会删除）。
- 有任何失败项，记录错误信息，报告 D 爷。

---

## 4. 完成标准

- [ ] `.dockerignore` 已创建，排除无用构建上下文
- [ ] Mosquitto 密码文件已预先生成，匿名访问被拒，凭证连接成功，密码文件权限为 600
- [ ] 五种服务的 Dockerfile 均可用 `docker build` 成功构建（**每个镜像均安装了 curl**）
- [ ] `docker compose -f docker-compose.full.yml config` 无语法错误
- [ ] `docker compose -f docker-compose.full.yml up` 后 9 个核心服务健康
- [ ] **TimescaleDB 服务可用，`IoTHunterTS` 数据库可达**
- [ ] **Kafka healthcheck 使用创建/删除临时 Topic 方式，无残留**
- [ ] 双协议数据全链路贯通（HTTP + MQTT）
- [ ] UI 核心接口（`/api/v1/config`、`/api/v2/traces/{deviceId}`、`/api/simulator/snapshot`）在容器化后均返回正确数据
- [ ] Gateway 后台服务异常不杀死宿主进程（回溯修复验证通过）
- [ ] Live 验证 13 项全部勾选通过
- [ ] 无硬编码连接串或明文密码在镜像中
- [ ] 无 Mock 数据
- [ ] `IoTHunter.Infrastructure` 被正确引用

---

第五步开发计划 V8.0 最终版完毕。D爷，所有裁决已熔接，curl 已内置，Kafka healthcheck 已增强，TimescaleDB 已预留，端口冲突已解决，密码文件权限已收紧。请定稿。