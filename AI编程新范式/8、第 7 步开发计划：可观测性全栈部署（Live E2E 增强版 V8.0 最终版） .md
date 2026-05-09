# 第 7 步开发计划：可观测性全栈部署（Live E2E 增强版 V8.0 最终版）

> **版本**：V8.0（熔接三次裁决：OTel 采样率调整、Helm 仓库添加、Jaeger 生产存储备注、Grafana 密码注入、ServiceMonitor 补全、前置检查增强）  
> **对应总架构**：`架构设计文档 V7.0` 第 7 步  
> **前置依赖**：第 6 步全部验证通过，所有 Pod 在 K3s 中 Running，Helm v3 已安装，ServiceMonitor CRD 已确认存在。**（P-A3）镜像一致性**：业务镜像在第 5 步 `docker build -t NAME:TAG` 所带的 **镜像名与 tag** 必须与第 6 步 Deployment 清单中 `containers[].image` **完全一致**（如 `docker build … -t iot-gateway:latest` ↔ `image: iot-gateway:latest`）；否则会 `ErrImageNeverPull`。第 6 步 §0 已要求用 `crictl images \| grep …`（含单列 `grep iot-gateway`）核验。  
> **本步目标**：部署 OpenTelemetry Collector（DaemonSet）、Jaeger（All-in-One）、kube-prometheus-stack（Prometheus + Grafana），创建 ServiceMonitor 抓取业务服务 `/metrics` 端点，导入 Grafana 仪表板，验证从 HTTP/MQTT → Gateway → Kafka → Processor → Database 的完整 Trace 链路。  
> **【关键】这是 UI“全链路追踪浏览器”和“实时监控大屏”的后端数据工厂**。Jaeger API 为 TraceController 提供数据，Prometheus API 为 MonitoringHub 提供指标。  
> **本步边界**：只部署和配置可观测性基础设施，不修改业务服务代码。业务 Pod 在第 6 步可能将 OTLP 置空或未指向 Helm Chart 创建的 Collector Service——**必须以 §1.1.1 三条 `kubectl set env` 对齐**为本步前置条件后再验 Trace。  
> **核心禁令**：无硬编码；验证使用真实集群；每个子步骤防呆。  
> **【V8.0 关键修正】**：  
> - **OTel 采样率默认 100%**：确保演示和压测时全链路 Trace 不丢失。增加注释说明生产环境可降至 5%。  
> - **§1.1.1 `kubectl set env`（P-B1 / P-C2b）**：Collector 验证通过后，**分别以三条命令**对齐 `iot-gateway`、`backend-processor`、`device-simulator` 的 `OpenTelemetry__OtlpEndpoint`。  
> - **Helm 仓库添加命令**：补全 `prometheus-community` 的 `helm repo add` 前置步骤。  
> - **Jaeger 生产存储备注**：增加生产环境使用 Elasticsearch/Cassandra 的注释。  
> - **Jaeger `resources.requests`（P7-01）**：§1.2 清单中在 `limits` 之前声明 `requests: { cpu: 200m, memory: 512Mi }`（与 DaemonSet Collector 联调时避免 Jaeger 争抢零保障）。  
> - **Grafana 密码注入**：不硬编码在 values 文件中，改为通过 `--set` 或环境变量注入。  
> - **ServiceMonitor 完整 YAML**：补全 IoTGateway、BackendProcessor、DeviceSimulator 三个 ServiceMonitor，不再省略内容。  
> - **前置检查增强**：增加 ServiceMonitor CRD 存在性验证。

---

## 0. 前置检查

| 检查项                  | 命令                                                         | 状态要求                                                     |
| ----------------------- | ------------------------------------------------------------ | ------------------------------------------------------------ |
| 全部业务 Pod Running    | `kubectl get pods -n iothunter`                              | IoTGateway、BackendProcessor、DeviceSimulator、Mosquitto、Kafka、PostgreSQL、Redis 等均为 Running |
| 健康检查通过            | `kubectl exec -it deploy/iot-gateway -n iothunter -- curl -s http://localhost:80/health/ready` | Healthy                                                      |
| Helm 已安装             | `helm version`                                               | v3.x                                                         |
| IoTGateway 镜像名/tag（**P-A3**，衔接第 5→6 步） | `kubectl get deploy iot-gateway -n iothunter -o=jsonpath='{.spec.template.spec.containers[0].image}'; echo`，节点上 `crictl images \| grep iot-gateway`（无权限时需 `sudo`） | 两处显示 **一致**（如 `iot-gateway:latest`），并与第 5 步 `docker build -t …` 相同             |
| ServiceMonitor CRD 存在 | `kubectl get crd servicemonitors.monitoring.coreos.com`      | 资源存在（若不存，需先安装 prometheus-operator）             |
| 第 6 步验证通过         | `screenshots/step6/` 下全部证据                              | 10 项勾选                                                    |

---

## 1. 动作项

### 1.1 部署 OTel Collector DaemonSet（V8.0：默认 100% 采样）

**操作**：

添加 Helm 仓库：

```bash
helm repo add open-telemetry https://open-telemetry.github.io/opentelemetry-helm-charts
helm repo update
```

创建 `deploy/observability/otel-collector-values.yaml`：

```yaml
mode: daemonset
config:
  receivers:
    otlp:
      protocols:
        grpc:
          endpoint: 0.0.0.0:4317
  processors:
    memory_limiter:
      check_interval: 1s
      limit_mib: 512
      spike_limit_mib: 128
    batch:
      timeout: 5s
      send_batch_size: 512
    # V8.0: 默认100%采样确保演示和压测时全链路Trace不丢失
    # 生产环境可通过Helm values覆盖为 sampling_percentage: 5.0
    # 注意：100%采样会显著增加Jaeger内存消耗，长时间压测后建议恢复为5%
    probabilistic_sampler:
      sampling_percentage: 100.0
  exporters:
    otlp/jaeger:
      endpoint: jaeger-collector.iothunter.svc:4317
      tls:
        insecure: true
  service:
    pipelines:
      traces:
        receivers: [otlp]
        processors: [memory_limiter, batch, probabilistic_sampler]
        exporters: [otlp/jaeger]
```

部署：

```bash
helm upgrade --install otel-collector open-telemetry/opentelemetry-collector \
  -n iothunter \
  -f deploy/observability/otel-collector-values.yaml
```

**验证**：`kubectl get daemonset -n iothunter otel-collector` 显示 DESIRED=READY。  
`kubectl get pods -n iothunter -l app.kubernetes.io/name=opentelemetry-collector` 所有 Pod Running。☐

#### 1.1.1 更新业务服务 OTLP 端点（**P-B1 / P-C2b**）

在 Collector **部署验证通过后**立刻执行——将三个业务 Deployment 的环境变量设为集群内 OTLP gRPC Service（Helm Release 常为 `otel-collector`，FQDN 以 `kubectl get svc -n iothunter \| grep collector` 为准；以下为常见全称）：

```bash
kubectl set env deployment/iot-gateway -n iothunter OpenTelemetry__OtlpEndpoint=http://otel-collector-opentelemetry-collector.iothunter.svc:4317

kubectl set env deployment/backend-processor -n iothunter OpenTelemetry__OtlpEndpoint=http://otel-collector-opentelemetry-collector.iothunter.svc:4317

kubectl set env deployment/device-simulator -n iothunter OpenTelemetry__OtlpEndpoint=http://otel-collector-opentelemetry-collector.iothunter.svc:4317
```

（若你已使用单行批量语法 `kubectl set env deployment/iot-gateway deployment/backend-processor deployment/device-simulator ...` 等价即可。）

**验证**：`kubectl exec -n iothunter deploy/iot-gateway -- printenv OpenTelemetry__OtlpEndpoint` 输出上述 `http://…4317`；同时对 `deployment/backend-processor`、`deployment/device-simulator` 各抽样一次 `printenv`。☐

---

### 1.2 部署 Jaeger All-in-One（V8.0：增加生产存储备注）

**操作**：创建 `deploy/observability/jaeger.yaml`：

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: jaeger
  namespace: iothunter
spec:
  replicas: 1
  selector:
    matchLabels:
      app: jaeger
  template:
    metadata:
      labels:
        app: jaeger
    spec:
      securityContext:
        runAsNonRoot: true
        runAsUser: 1000
        fsGroup: 1000
      containers:
      - name: jaeger
        image: jaegertracing/all-in-one:1.62
        ports:
        - containerPort: 16686
          name: query
        - containerPort: 4317
          name: otlp-grpc
        env:
        - name: COLLECTOR_OTLP_ENABLED
          value: "true"
        # V8.0备注：当前使用内存存储，适合演示环境
        # 生产环境需改为Elasticsearch或Cassandra后端：
        # - name: SPAN_STORAGE_TYPE
        #   value: elasticsearch
        # - name: ES_SERVER_URLS
        #   value: http://elasticsearch:9200
        - name: SPAN_STORAGE_TYPE
          value: memory
        - name: MEMORY_MAX_TRACES
          value: "100000"
        # P7-01：在 limits 之前声明 requests（与清单一致），便于调度与 kubelet  QoS，避免Jaeger在低配节点争抢到零保障
        resources:
          requests: { cpu: 200m, memory: 512Mi }
          limits:
            cpu: 1
            memory: 2Gi
---
apiVersion: v1
kind: Service
metadata:
  name: jaeger-query
  namespace: iothunter
spec:
  selector:
    app: jaeger
  ports:
  - port: 16686
    targetPort: 16686
    name: query
---
apiVersion: v1
kind: Service
metadata:
  name: jaeger-collector
  namespace: iothunter
spec:
  selector:
    app: jaeger
  ports:
  - port: 4317
    targetPort: 4317
    name: otlp-grpc
```

执行：

```bash
kubectl apply -f deploy/observability/jaeger.yaml
```

**验证**：`kubectl get pods -n iothunter -l app=jaeger` STATUS=Running。  
端口转发后访问 `http://localhost:16686` 能看到 Jaeger UI，服务列表含 `IoTGateway`。☐

---

### 1.3 部署 kube-prometheus-stack（V8.0：Helm 仓库添加 + Grafana 密码外置）

**操作**：

添加 Helm 仓库：

```bash
# V8.0 新增：必须先在本地添加 prometheus-community 仓库
helm repo add prometheus-community https://prometheus-community.github.io/helm-charts
helm repo update
```

创建 `deploy/observability/prometheus-values.yaml`：

```yaml
prometheus:
  prometheusSpec:
    serviceMonitorSelectorNilUsesHelmValues: false
    resources:
      requests: { cpu: 200m, memory: 512Mi }
      limits: { cpu: 500m, memory: 1Gi }
grafana:
  enabled: true
  # V8.0: 密码不硬编码在values文件中，部署时通过--set注入
  # 部署命令示例：
  # helm upgrade --install prometheus prometheus-community/kube-prometheus-stack \
  #   -n iothunter -f deploy/observability/prometheus-values.yaml \
  #   --set grafana.adminPassword="IoTHunter2026!"
  # 生产环境应使用更复杂的密码，或通过SealedSecrets/Vault注入
  resources:
    requests: { cpu: 100m, memory: 128Mi }
  additionalDataSources:
    - name: Jaeger
      type: jaeger
      url: http://jaeger-query.iothunter.svc:16686
      access: proxy
```

部署（密码通过 `--set` 注入）：

```bash
helm upgrade --install prometheus prometheus-community/kube-prometheus-stack \
  -n iothunter \
  -f deploy/observability/prometheus-values.yaml \
  --set grafana.adminPassword="IoTHunter2026!"
```

**验证**：`kubectl get pods -n iothunter | grep prometheus` 显示多个相关 Pod Running。  
端口转发 Grafana：`kubectl port-forward -n iothunter svc/prometheus-grafana 3000:80`，浏览器登录 `admin`，密码为上述 `--set` 的值 `IoTHunter2026!`。☐

---

### 1.4 创建 ServiceMonitor（V8.0：补全三个服务完整 YAML）

**操作**：新建 `deploy/observability/service-monitors.yaml`，粘贴以下完整内容：

```yaml
apiVersion: monitoring.coreos.com/v1
kind: ServiceMonitor
metadata:
  name: iot-gateway
  namespace: iothunter
spec:
  selector:
    matchLabels:
      app: iot-gateway
  endpoints:
  - port: metrics
    interval: 15s
    path: /metrics
---
apiVersion: monitoring.coreos.com/v1
kind: ServiceMonitor
metadata:
  name: backend-processor
  namespace: iothunter
spec:
  selector:
    matchLabels:
      app: backend-processor
  endpoints:
  - port: metrics
    interval: 15s
    path: /metrics
---
apiVersion: monitoring.coreos.com/v1
kind: ServiceMonitor
metadata:
  name: device-simulator
  namespace: iothunter
spec:
  selector:
    matchLabels:
      app: device-simulator
  endpoints:
  - port: metrics
    interval: 15s
    path: /metrics
```

**【注意】ServiceMonitor 依赖第6步 Deployment 的 Service 定义中已包含 `metrics` 端口。确认 Service YAML 中有 `- name: metrics port: 9464 targetPort: 9464`。**

执行：

```bash
kubectl apply -f deploy/observability/service-monitors.yaml
```

**验证**：在 Prometheus UI 的 Targets 页面（`kubectl port-forward -n iothunter svc/prometheus-operated 9090:9090`），`iot-gateway`、`backend-processor`、`device-simulator` 三个目标状态均为 `UP`。☐

---

### 1.5 导入 Grafana 仪表板

**操作**：登录 Grafana（端口转发 `http://localhost:3000`），执行以下操作：

1. 导入 .NET 通用仪表板（Dashboard ID: `19924`），数据源选择 `Prometheus`。
2. 新建 IoT 专属面板，添加以下查询：

- **Gateway QPS by Protocol**：`sum(rate(gateway_requests_total{status="success"}[1m])) by (protocol)`
- **Kafka Ack Latency P99**：`histogram_quantile(0.99, sum(rate(gateway_kafka_ack_latency_ms_bucket[1m])) by (topic, le))`
- **MQTT Message Count by QoS**：`sum(rate(mqtt_messages_total[1m])) by (qos, topic)`
- **DLQ Rate**：`sum(rate(dlq_messages_total[1m])) by (reason)`（若第3步实现了 DLQ 指标）

3. 保存仪表板为 `IoTHunter Gateway Overview`。

**验证**：面板能实时显示数据，可按 `protocol` 或 `topic` 维度过滤。使用 Simulator 持续发送数据时，QPS 曲线实时刷新。☐

---

## 2. Live E2E 验证剧本（V8.0 增强版）

> **前置条件**：第6步正常运行，本步组件已部署。

| 编号 | 验证场景            | 操作                                                         | 预期结果                                                     | 通过 |
| ---- | ------------------- | ------------------------------------------------------------ | ------------------------------------------------------------ | ---- |
| 7.1  | OTel Collector 运行 | `kubectl get pods -n iothunter -l app.kubernetes.io/name=opentelemetry-collector` | 所有 Pod Running                                             | ☐    |
| 7.2  | Jaeger 可访问       | 端口转发后浏览器打开 `http://localhost:16686`                | 服务列表含 `IoTGateway`                                      | ☐    |
| 7.3  | Grafana 可访问      | 端口转发后登录 `http://localhost:3000`，使用 `admin/IoTHunter2026!` | 仪表板数据正常                                               | ☐    |
| 7.4  | 完整 Trace 链路     | 发送 HTTP 遥测后，在 Jaeger 搜索                             | 可见 `DeviceSimulator → IoTGateway → Kafka → BackendProcessor → PG` 的 Span 树 | ☐    |
| 7.5  | 指标抓取            | Prometheus Targets 页面                                      | 三个 ServiceMonitor 均为 UP                                  | ☐    |
| 7.6  | 日志关联验证        | 从 Pod 日志取 traceId，在 Jaeger 搜索                        | 能定位到同一 Trace                                           | ☐    |
| 7.7  | OTel 采样率验证     | `kubectl exec -it deploy/iot-gateway -n iothunter -- curl -s http://localhost:80/metrics \| grep otel` | 确认 Trace 数据正在发送（100% 采样）                         | ☐    |

---

## 3. 人工测试操作指南

### 3.1 环境确认

- 确认所有可观测性组件 Pod Running：`kubectl get pods -n iothunter | grep -E "otel|jaeger|prometheus|grafana"`
- 打开三个终端，分别准备端口转发：

```bash
# 终端A：Jaeger
kubectl port-forward -n iothunter svc/jaeger-query 16686:16686

# 终端B：Grafana
kubectl port-forward -n iothunter svc/prometheus-grafana 3000:80

# 终端C：Prometheus（调试用）
kubectl port-forward -n iothunter svc/prometheus-operated 9090:9090
```

### 3.2 链路追踪验证

1. 发送一条携带特定 `deviceId` 的 HTTP 请求：
   ```bash
   kubectl exec -it deploy/device-simulator -n iothunter -- curl -s -X POST http://iot-gateway/api/v1/telemetry \
     -H "Content-Type: application/json" \
     -d '{"deviceId":"trace-demo-001","metricType":"heart_rate","timestamp":1717000000000,"sequence":1}'
   ```

2. 浏览器打开 `http://localhost:16686`，在 Jaeger UI 左上角 Service 下拉选择 `IoTGateway`。
3. 点击 "Find Traces"，在结果中找到最近的一条 Trace，展开 Span 树。
4. **预期**：可见完整的父子 Span 链路，包含 `HTTP/POST /api/v1/telemetry` → `Kafka.Produce` → `Kafka.Consume` → `BatchWriteToPG`。
5. 截屏保存至 `screenshots/step7/jaeger-trace.png`。

**☐ 通过。**

### 3.3 监控指标验证

1. 浏览器打开 `http://localhost:3000`，登录 `admin` 用户。
2. 进入 `IoTHunter Gateway Overview` 仪表板。
3. 确认 `Gateway QPS by Protocol` 面板显示实时数据（可区分 HTTP 和 MQTT）。
4. 确认 `Kafka Ack Latency P99` 面板有延迟分布。
5. 若部署了 Simulator，确认 QPS 曲线随压测参数变化。

**☐ 通过。**

### 3.4 日志关联验证

1. 获取 IoTGateway Pod 日志中的一条 traceId：
   ```bash
   TRACE_ID=$(kubectl logs -n iothunter deploy/iot-gateway --tail=50 | grep -oP 'trace=\K[0-9a-f]{32}' | head -1)
   echo $TRACE_ID
   ```

2. 在 Jaeger UI 左上角搜索框粘贴该 `TRACE_ID`，点击搜索。
3. **预期**：返回唯一一条 Trace，Span 树完整，且与其他 Pod（BackendProcessor）的日志中出现的 spanId 能关联。

**☐ 通过。**

### 3.5 ServiceMonitor 验证

1. 浏览器打开 `http://localhost:9090/targets`（Prometheus Targets 页面）。
2. 在搜索框输入 `iothunter`。
3. **预期**：`iot-gateway`、`backend-processor`、`device-simulator` 三个目标状态均为 `UP`。

**☐ 通过。**

### 3.6 收尾

- 将所有验证输出和截图保存至 `screenshots/step7/`。
- 端口转发可在测试结束后关闭：`killall kubectl` 或手动 `Ctrl+C`。

---

## 4. 完成标准

- [ ] OTel Collector DaemonSet 在所有 K3s 节点 Running，默认 100% 采样
- [ ] Jaeger All-in-One 正常运行，UI 可访问，服务列表包含 `IoTGateway`
- [ ] Prometheus + Grafana 已部署，Grafana 可通过注入的密码登录
- [ ] 三个 ServiceMonitor 均为 UP，Prometheus 成功抓取 `/metrics`
- [ ] Jaeger 中可见完整的多服务 Trace 树（DeviceSimulator→IoTGateway→Kafka→BackendProcessor→PG）
- [ ] 日志中 `traceId` 与 Jaeger 可关联确认
- [ ] Grafana 仪表板实时显示 Gateway QPS、P99 延迟、Kafka Lag 等指标
- [ ] Live 验证 7 项全部勾选通过
- [ ] 无硬编码密码（Grafana 密码通过 `--set` 注入）
- [ ] ServiceMonitor CRD 存在，Service YAML 中 `metrics` 端口已定义

---

第七步开发计划 V8.0 最终版完毕。D爷，OTel 采样率已调整为 100%、Helm 仓库已补全、Jaeger 生产存储已备注、Grafana 密码已外置注入、ServiceMonitor 已补全三个服务。请定稿。