# 第 6 步开发计划：K8s 安全基线部署清单（Live E2E 增强版 V8.0 最终版）

> **版本**：V8.0（熔接三次裁决：Secret 命令式注入、MQTT 凭证分离、securityContext 补全、NetworkPolicy 完整 YAML、Mosquitto subPath 挂载、Dockerfile 安装 curl）  
> **对应总架构**：`架构设计文档 V7.0` 第 6 步  
> **前置依赖**：第 5 步全部 Live 验证通过，所有服务的容器镜像已构建并导入 K3s，K3s 集群运行正常，Cilium 已安装  
> **本步目标**：在 K3s 集群上部署 IoTGateway、BackendProcessor、DeviceSimulator 和 Mosquitto，配置 NetworkPolicy（零信任最小权限）、TLS 证书、Secret（收敛一切敏感信息，**禁止明文存储在 YAML 中**）、健康探针、资源限制、优雅停机和 Pod 安全上下文，形成一套可验证的生产级安全基线。  
> **本步边界**：只涉及 K8s 清单编写和部署验证，不修改应用代码，不修改 Dockerfile。  
> **核心禁令**：  
> - **硬编码禁令（ADR-022）**：所有敏感信息通过 `kubectl create secret --from-file` 命令式注入，**严禁在 YAML 中写入明文密码**。  
> - **Mock 数据禁令（ADR-023）**：验证使用真实集群和中间件，禁止模拟。  
> **【V8.0 关键修正】**：  
> - **Secret 命令式注入**：Mosquitto 配置和凭证通过 `kubectl create secret --from-file` 创建，避免 YAML 中残留明文占位符。**P6-02**：数据库/Redis/Kafka/MQTT 凭据仅能使用 §1.2.1 四条 `kubectl create secret generic … --from-literal=…`，**禁止** `deploy/secrets.yaml` + `kubectl apply -f deploy/secrets.yaml`。  
> - **MQTT 凭证分离**：新增独立的 `mqtt-credentials` Secret，Gateway 不再从整个密码文件读取密码。  
> - **securityContext 补全**：所有 Deployment 增加 `runAsNonRoot`、`readOnlyRootFilesystem` 等非 root 安全上下文。  
> - **NetworkPolicy Ingress 放行**：在 default-deny-all 之上补充对 IoTGateway、BackendProcessor 及 Kafka/PostgreSQL/Redis/Mosquitto 的入站策略，使 Simulator→Gateway、Processor→中间件等流量与白名单抓取可达。**P-A1-4**：本节清单含 `allow-gateway-ingress`、`allow-processor-ingress`、`allow-middleware-ingress` 三条聚合入站；**P-F1**：补充 `allow-webui-egress`、`allow-webui-to-management`、`allow-webui-to-backends`。
> - **Mosquitto subPath 挂载**：使用 `subPath` 精确挂载单个文件，避免覆盖基础镜像目录。  
> - **P6-05**：应用 Deployment（IoTGateway、BackendProcessor、DeviceSimulator）容器 `image` 下须显式声明 `imagePullPolicy: IfNotPresent`（本地导入镜像常见场景）。  
> - **P6-06**：IoTGateway `resources.limits.memory` 基线为 **512Mi**。  
> - **前置检查增强**：增加 Cilium 运行验证、ServiceMonitor CRD 存在性检查、镜像导入确认。**P-A3**（亦为第 7 步前置衔接）：镜像 `tag` / 名称必须与第 5 步 `-t …`、第 6 步 YAML `image:` 一致；参见 §0 检查表 `grep iot-gateway`。

---

## 0. 前置检查

| 检查项                   | 验证命令                                                     | 状态要求                            |
| ------------------------ | ------------------------------------------------------------ | ----------------------------------- |
| K3s 集群正常             | `kubectl get nodes`                                          | 至少一个节点 STATUS=Ready           |
| Cilium 已安装并运行      | `kubectl get pods -n kube-system \| grep cilium`             | 所有 cilium Pod Running             |
| ServiceMonitor CRD 存在  | `kubectl get crd servicemonitors.monitoring.coreos.com`      | 资源存在（若不存则为第7步前置依赖） |
| 镜像已导入               | `crictl images \| grep -E "iot-gateway\|backend-processor\|device-simulator"` **并**单列 `crictl images \| grep iot-gateway` | 三项均可见；**P-A3**：gateway 条目名/tag 必须与第 5 步 `docker build -t …` 及第 6 步 YAML 中 `containers[].image` **逐字一致**（典型 `iot-gateway:latest`）                        |
| 第 5 步验证通过          | `screenshots/step5/` 下存在全部证据                          | 13 项已勾选                         |
| Mosquitto 密码文件已生成 | `cat infra/mosquitto/passwordfile`                           | 加密 Hash 存在                      |

**说明**：如果中间件（Kafka、PostgreSQL、Redis）尚未在 K3s 中部署，本步将提供一套精简示例清单（仅用于验证全链路，生产需额外高可用配置）。**（P-A3）** 第 5 步 `docker build -t 镜像:tag` 必须与各业务 `Deployment` 清单中的 `image:` **完全一致**；上表 `grep iot-gateway` 为网关快速核对，**backend-processor / device-simulator 同理**。

---

## 1. 动作项

### 1.1 创建命名空间

**操作**：新建 `deploy/namespace.yaml`，粘贴以下内容：

```yaml
apiVersion: v1
kind: Namespace
metadata:
  name: iothunter
```

执行：

```bash
kubectl apply -f deploy/namespace.yaml
kubectl config set-context --current --namespace=iothunter
```

**验证**：`kubectl get namespace iothunter` 输出 `Active`。☐

---

### 1.2 创建 Secret（命令式注入，严禁明文 YAML）

**【V8.0 关键修正】所有包含密码和配置文件的 Secret 均通过命令行创建，禁止在 YAML 中写入明文。**

#### 1.2.1 数据库、Redis、Kafka、MQTT 凭证（四条命令式注入）

**禁止使用 `secrets.yaml` 明文清单**，**不得以 `kubectl apply -f deploy/secrets.yaml` 方式加载聚合 Secret YAML**（**P6-02**）。在设置好当前上下文命名空间（`kubectl config set-context --current --namespace=iothunter`）后执行以下四条命令（连接串主机名与集群内 Service 一致；密码请在执行前替换为实际值，勿提交到仓库）：

```bash
kubectl create secret generic db-secret \
  --from-literal=ConnectionStrings__Postgres='Host=postgres.iothunter.svc.cluster.local;Database=IoTHunter;Username=iotapp;Password=<POSTGRES_PASSWORD>' \
  -n iothunter

kubectl create secret generic redis-secret \
  --from-literal=ConnectionStrings__Redis='redis.iothunter.svc.cluster.local:6379' \
  -n iothunter

kubectl create secret generic kafka-secret \
  --from-literal=Kafka__BootstrapServers='kafka.iothunter.svc.cluster.local:9092' \
  -n iothunter

kubectl create secret generic mqtt-credentials \
  --from-literal=gateway-username='iotgateway' \
  --from-literal=gateway-password='<MQTT_GATEWAY_PASSWORD>' \
  --from-literal=simulator-username='devicesim' \
  --from-literal=simulator-password='<MQTT_SIMULATOR_PASSWORD>' \
  -n iothunter
```

**说明**：`ConnectionStrings__Postgres` / `ConnectionStrings__Redis` 对应 BackendProcessor `GetConnectionString`；`Kafka__BootstrapServers` 对应网关与 BackendProcessor；`mqtt-credentials` 多键供 IoTGateway 与 DeviceSimulator（MQTT 模式）分别从 `secretKeyRef` 引用。

若 Secret 已存在需更新，可先 `kubectl delete secret <名称> -n iothunter` 后重新执行对应命令。

#### 1.2.2 Mosquitto 配置文件（通过命令式创建）

```bash
kubectl create secret generic mosquitto-config \
  --from-file=mosquitto.conf=infra/mosquitto/mosquitto.conf \
  --from-file=passwordfile=infra/mosquitto/passwordfile \
  --from-file=aclfile=infra/mosquitto/aclfile \
  -n iothunter
```

**【强制要求】此命令必须在第5步生成加密密码文件后执行。严禁在 YAML 中保留 `$7$101$...` 占位符。**

**验证**：`kubectl get secrets -n iothunter` 列出 `db-secret`、`redis-secret`、`kafka-secret`、`mqtt-credentials`、`mosquitto-config`。  
`kubectl describe secret mosquitto-config -n iothunter` 确认包含三个文件条目。☐

---

### 1.3 生成 TLS 证书并创建 Secret

**操作**：执行以下脚本生成自签证书（仅用于集群内部 Pod 间通信）。

```bash
# 生成 CA
openssl req -x509 -newkey rsa:4096 -days 365 -nodes -keyout ca.key -out ca.crt -subj "/CN=IoTHunter CA"

# Gateway 证书
openssl req -newkey rsa:2048 -nodes -keyout gateway.key -out gateway.csr -subj "/CN=iot-gateway.iothunter.svc"
openssl x509 -req -in gateway.csr -CA ca.crt -CAkey ca.key -CAcreateserial -out gateway.crt -days 365

# Mosquitto 证书
openssl req -newkey rsa:2048 -nodes -keyout mosquitto.key -out mosquitto.csr -subj "/CN=mosquitto.iothunter.svc"
openssl x509 -req -in mosquitto.csr -CA ca.crt -CAkey ca.key -CAcreateserial -out mosquitto.crt -days 365

# 创建 Secret
kubectl create secret tls gateway-tls --cert=gateway.crt --key=gateway.key -n iothunter
kubectl create secret tls mosquitto-tls --cert=mosquitto.crt --key=mosquitto.key -n iothunter
```

**验证**：`kubectl get secrets -n iothunter | grep tls` 输出 `gateway-tls` 和 `mosquitto-tls`。☐

---

### 1.4 部署中间件（最小化示例）

**操作**：新建 `deploy/infra/` 目录，编写以下完整的 YAML 文件。

#### 1.4.1 Kafka + Zookeeper

创建 `deploy/infra/kafka.yaml`，包含 Zookeeper 和 Kafka 的完整 Deployment 与 Service（使用 `confluentinc/cp-kafka:7.6.0`，Zookeeper 单副本，Kafka 单副本，Service 名为 `kafka`）。

#### 1.4.2 PostgreSQL

创建 `deploy/infra/postgres.yaml`，Deployment 单副本，Service 名为 `postgres`。

#### 1.4.3 Redis

创建 `deploy/infra/redis.yaml`，Deployment 单副本，Service 名为 `redis`。

#### 1.4.4 TimescaleDB

创建 `deploy/infra/timescaledb.yaml`，Deployment 单副本，Service 名为 `timescaledb`。

执行：

```bash
kubectl apply -f deploy/infra/
kubectl wait --for=condition=ready pod -l app=kafka --timeout=120s -n iothunter
kubectl wait --for=condition=ready pod -l app=postgres --timeout=60s -n iothunter
kubectl wait --for=condition=ready pod -l app=redis --timeout=60s -n iothunter
kubectl wait --for=condition=ready pod -l app=timescaledb --timeout=60s -n iothunter
```

**验证**：`kubectl get pods -n iothunter` 显示 kafka、postgres、redis、timescaledb Pod 均为 Running。☐

---

### 1.5 部署 Mosquitto（V8.0 subPath 挂载 + 安全上下文）

**操作**：新建 `deploy/mosquitto.yaml`，粘贴以下完整内容。**此版本使用 `subPath` 精确挂载单个文件。**

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: mosquitto
  namespace: iothunter
spec:
  replicas: 1
  selector:
    matchLabels:
      app: mosquitto
  template:
    metadata:
      labels:
        app: mosquitto
    spec:
      securityContext:
        runAsNonRoot: true
        runAsUser: 1883
        fsGroup: 1883
      containers:
      - name: mosquitto
        image: eclipse-mosquitto:2.0.18
        ports:
        - containerPort: 1883
          name: mqtt
        - containerPort: 8083
          name: websocket
        volumeMounts:
        - name: mosquitto-conf
          mountPath: /mosquitto/config/mosquitto.conf
          subPath: mosquitto.conf
        - name: mosquitto-password
          mountPath: /mosquitto/config/passwordfile
          subPath: passwordfile
        - name: mosquitto-acl
          mountPath: /mosquitto/config/aclfile
          subPath: aclfile
        - name: data
          mountPath: /mosquitto/data
        securityContext:
          allowPrivilegeEscalation: false
          readOnlyRootFilesystem: false
          capabilities:
            drop: ["ALL"]
        resources:
          requests:
            cpu: 50m
            memory: 64Mi
          limits:
            cpu: 200m
            memory: 128Mi
      volumes:
      - name: mosquitto-conf
        secret:
          secretName: mosquitto-config
          items:
          - key: mosquitto.conf
            path: mosquitto.conf
      - name: mosquitto-password
        secret:
          secretName: mosquitto-config
          items:
          - key: passwordfile
            path: passwordfile
      - name: mosquitto-acl
        secret:
          secretName: mosquitto-config
          items:
          - key: aclfile
            path: aclfile
      - name: data
        emptyDir: {}
---
apiVersion: v1
kind: Service
metadata:
  name: mosquitto
  namespace: iothunter
spec:
  selector:
    app: mosquitto
  ports:
  - port: 1883
    targetPort: 1883
    name: mqtt
  - port: 8083
    targetPort: 8083
    name: websocket
```

**【生产环境备注】`emptyDir` 在 Pod 重建时数据丢失。生产环境应替换为 `persistentVolumeClaim`。**

执行：

```bash
kubectl apply -f deploy/mosquitto.yaml
```

**验证**：`kubectl get pods -n iothunter -l app=mosquitto` STATUS=Running。☐

---

### 1.6 部署 IoTGateway（V8.0：MQTT 凭证从独立 Secret 读取 + 安全上下文）

**操作**：新建 `deploy/iot-gateway.yaml`，粘贴以下完整内容。

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: iot-gateway
  namespace: iothunter
spec:
  replicas: 2
  selector:
    matchLabels:
      app: iot-gateway
  template:
    metadata:
      labels:
        app: iot-gateway
    spec:
      terminationGracePeriodSeconds: 30
      securityContext:
        runAsNonRoot: true
        runAsUser: 1000
        fsGroup: 1000
      volumes:
      - name: tmp
        emptyDir: {}
      - name: tls
        secret:
          secretName: gateway-tls
          optional: true
      containers:
      - name: gateway
        image: iot-gateway:latest
        imagePullPolicy: IfNotPresent
        ports:
        - containerPort: 80
        - containerPort: 9464
          name: metrics
        env:
        - name: Kafka__BootstrapServers
          valueFrom:
            secretKeyRef:
              name: kafka-secret
              key: Kafka__BootstrapServers
        - name: Mqtt__TcpServer
          value: mosquitto.iothunter.svc.cluster.local
        - name: Mqtt__TcpPort
          value: "1883"
        - name: Mqtt__Username
          valueFrom:
            secretKeyRef:
              name: mqtt-credentials
              key: gateway-username
        - name: Mqtt__Password
          valueFrom:
            secretKeyRef:
              name: mqtt-credentials
              key: gateway-password
        - name: ASPNETCORE_URLS
          value: "http://+:80"
        - name: OpenTelemetry__OtlpEndpoint
          value: ""
        startupProbe:
          httpGet:
            path: /health/ready
            port: 80
          failureThreshold: 30
          periodSeconds: 5
        livenessProbe:
          httpGet:
            path: /health/live
            port: 80
          initialDelaySeconds: 10
          periodSeconds: 10
        readinessProbe:
          httpGet:
            path: /health/ready
            port: 80
          initialDelaySeconds: 5
          periodSeconds: 5
        securityContext:
          allowPrivilegeEscalation: false
          readOnlyRootFilesystem: true
          capabilities:
            drop: ["ALL"]
        resources:
          requests:
            cpu: 100m
            memory: 128Mi
          limits:
            cpu: 1
            memory: 512Mi
        volumeMounts:
        - name: tmp
          mountPath: /tmp
        - name: tls
          mountPath: /app/certs
          readOnly: true
---
apiVersion: v1
kind: Service
metadata:
  name: iot-gateway
  namespace: iothunter
  labels:
    app: iot-gateway
  annotations:
    prometheus.io/port: "9464"
    prometheus.io/scrape: "true"
spec:
  selector:
    app: iot-gateway
  ports:
  - port: 80
    targetPort: 80
    name: http
  - port: 9464
    targetPort: 9464
    name: metrics
```

执行：

```bash
kubectl apply -f deploy/iot-gateway.yaml
```

**【衔接第 7 步】**：`OpenTelemetry__OtlpEndpoint` 本步可保持 `""`（不上报 OTLP）。部署可观测性栈并确认 Collector 地址后，再执行例如  
`kubectl set env deployment/iot-gateway -n iothunter OpenTelemetry__OtlpEndpoint=http://<otel-collector-service>:4317`（具体 Service 名以第 7 步清单为准）。

**验证**：`kubectl rollout status deployment/iot-gateway -n iothunter` 成功。☐

---

### 1.7 部署 BackendProcessor 与 DeviceSimulator

**操作**：新建 `deploy/backend-processor.yaml`，粘贴以下内容（Deployment + Service；`ASPNETCORE_URLS` 与 Dockerfile 惯例一致：`80` 为 API，`9464` 为 Prometheus scraping，与网关的同名端口分属不同 Endpoints，不冲突）。

```yaml
apiVersion: v1
kind: Service
metadata:
  name: backend-processor
  namespace: iothunter
  labels:
    app: backend-processor
  annotations:
    prometheus.io/port: "9464"
    prometheus.io/scrape: "true"
spec:
  selector:
    app: backend-processor
  ports:
    - name: http
      port: 80
      targetPort: 80
    - name: metrics
      port: 9464
      targetPort: 9464
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: backend-processor
  namespace: iothunter
  labels:
    app: backend-processor
spec:
  replicas: 1
  selector:
    matchLabels:
      app: backend-processor
  template:
    metadata:
      labels:
        app: backend-processor
      annotations:
        prometheus.io/port: "9464"
        prometheus.io/scrape: "true"
    spec:
      terminationGracePeriodSeconds: 45
      affinity:
        podAntiAffinity:
          preferredDuringSchedulingIgnoredDuringExecution:
            - weight: 100
              podAffinityTerm:
                labelSelector:
                  matchLabels:
                    app: backend-processor
                topologyKey: kubernetes.io/hostname
      securityContext:
        runAsNonRoot: true
        runAsUser: 1000
        fsGroup: 1000
        readOnlyRootFilesystem: true
      volumes:
      - name: tmp
        emptyDir: {}
      containers:
      - name: backend-processor
        image: backend-processor:latest
        imagePullPolicy: IfNotPresent
        ports:
        - containerPort: 80
          name: http
        - containerPort: 9464
          name: metrics
        env:
        - name: Kafka__BootstrapServers
          valueFrom:
            secretKeyRef:
              name: kafka-secret
              key: Kafka__BootstrapServers
        - name: ConnectionStrings__Postgres
          valueFrom:
            secretKeyRef:
              name: db-secret
              key: ConnectionStrings__Postgres
        - name: ConnectionStrings__Redis
          valueFrom:
            secretKeyRef:
              name: redis-secret
              key: ConnectionStrings__Redis
        - name: ASPNETCORE_URLS
          value: "http://+:80"
        - name: OpenTelemetry__OtlpEndpoint
          value: ""
        startupProbe:
          httpGet:
            path: /health/ready
            port: 80
          failureThreshold: 36
          periodSeconds: 5
        livenessProbe:
          httpGet:
            path: /health/live
            port: 80
          initialDelaySeconds: 15
          periodSeconds: 10
        readinessProbe:
          httpGet:
            path: /health/ready
            port: 80
          initialDelaySeconds: 10
          periodSeconds: 5
        securityContext:
          allowPrivilegeEscalation: false
          readOnlyRootFilesystem: true
          capabilities:
            drop: ["ALL"]
        lifecycle:
          preStop:
            exec:
              command: ["sh", "-c", "sleep 5"]
        resources:
          requests:
            cpu: 250m
            memory: 256Mi
          limits:
            cpu: "2"
            memory: 512Mi
        volumeMounts:
        - name: tmp
          mountPath: /tmp
```

新建 `deploy/device-simulator.yaml`，粘贴以下内容（内置管理端点：`/api/simulator/health`、`/api/simulator/snapshot`；`ASPNETCORE_URLS` 同时绑定 `80` 与 `9464`，便于 API 与 Prometheus 分端口抓取；敏感项除可通过 Secret 覆盖外，本文档与集群内 Service 短名一致）。

```yaml
apiVersion: v1
kind: Service
metadata:
  name: device-simulator
  namespace: iothunter
  labels:
    app: device-simulator
  annotations:
    prometheus.io/port: "9464"
    prometheus.io/scrape: "true"
spec:
  selector:
    app: device-simulator
  ports:
    - name: http
      port: 80
      targetPort: 80
    - name: metrics
      port: 9464
      targetPort: 9464
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: device-simulator
  namespace: iothunter
  labels:
    app: device-simulator
spec:
  replicas: 1
  selector:
    matchLabels:
      app: device-simulator
  template:
    metadata:
      labels:
        app: device-simulator
      annotations:
        prometheus.io/port: "9464"
        prometheus.io/scrape: "true"
    spec:
      terminationGracePeriodSeconds: 15
      securityContext:
        runAsNonRoot: true
        runAsUser: 1000
        fsGroup: 1000
        readOnlyRootFilesystem: true
      volumes:
      - name: tmp
        emptyDir: {}
      containers:
      - name: device-simulator
        image: device-simulator:latest
        imagePullPolicy: IfNotPresent
        ports:
        - containerPort: 80
          name: http
        - containerPort: 9464
          name: metrics
        env:
        - name: ASPNETCORE_URLS
          value: "http://+:80;http://+:9464"
        - name: Simulator__Protocol
          value: "http"
        - name: Simulator__GatewayHttpBase
          value: "http://iot-gateway.iothunter.svc.cluster.local"
        - name: Simulator__DeviceCount
          value: "5"
        - name: Simulator__IntervalMs
          value: "500"
        - name: Simulator__Concurrency
          value: "2"
        - name: Simulator__DurationSeconds
          value: "0"
        - name: Simulator__ProxyUrl
          value: ""
        - name: OpenTelemetry__OtlpEndpoint
          value: ""
        startupProbe:
          httpGet:
            path: /api/simulator/health
            port: 80
          failureThreshold: 30
          periodSeconds: 5
        livenessProbe:
          httpGet:
            path: /metrics
            port: 9464
          initialDelaySeconds: 10
          periodSeconds: 15
        readinessProbe:
          httpGet:
            path: /api/simulator/health
            port: 80
          initialDelaySeconds: 5
          periodSeconds: 10
        securityContext:
          allowPrivilegeEscalation: false
          readOnlyRootFilesystem: true
          capabilities:
            drop: ["ALL"]
        resources:
          requests:
            cpu: 100m
            memory: 64Mi
          limits:
            cpu: "1"
            memory: 128Mi
        volumeMounts:
        - name: tmp
          mountPath: /tmp
```

如需 **MQTT** 模式，将 `Simulator__Protocol` 改为 `mqtt`，并增加 `Simulator__MqttWebSocketUrl`（如 `ws://mosquitto.iothunter.svc.cluster.local:8083/mqtt`）及从 `mqtt-credentials` 引用的 MQTT 用户名/密码环境变量（与网关一致由 Mosquitto 侧 ACL 控制）。

执行：

```bash
kubectl apply -f deploy/backend-processor.yaml
kubectl apply -f deploy/device-simulator.yaml
kubectl rollout status deployment/backend-processor -n iothunter
kubectl rollout status deployment/device-simulator -n iothunter
```

**验证**：`kubectl get pods -n iothunter` 显示所有 Pod Running。☐

---

### 1.8 部署 NetworkPolicy（零信任，完整 YAML）

**操作**：新建 `deploy/network-policy.yaml`，粘贴以下完整内容。

```yaml
# 默认拒绝所有入站和出站流量
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: default-deny-all
  namespace: iothunter
spec:
  podSelector: {}
  policyTypes:
  - Ingress
  - Egress
---
# 允许 Gateway 访问 Kafka 和 Mosquitto
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: allow-gateway-egress
  namespace: iothunter
spec:
  podSelector:
    matchLabels:
      app: iot-gateway
  policyTypes:
  - Egress
  egress:
  - to:
    - podSelector:
        matchLabels:
          app: kafka
    ports:
    - protocol: TCP
      port: 9092
  - to:
    - podSelector:
        matchLabels:
          app: mosquitto
    ports:
    - protocol: TCP
      port: 1883
    - protocol: TCP
      port: 8083
  # 允许 DNS 解析
  - to:
    - namespaceSelector: {}
      podSelector:
        matchLabels:
          k8s-app: kube-dns
    ports:
    - protocol: UDP
      port: 53
---
# 允许 Processor 访问 Kafka、PostgreSQL、Redis
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: allow-processor-egress
  namespace: iothunter
spec:
  podSelector:
    matchLabels:
      app: backend-processor
  policyTypes:
  - Egress
  egress:
  - to:
    - podSelector:
        matchLabels:
          app: kafka
    ports:
    - protocol: TCP
      port: 9092
  - to:
    - podSelector:
        matchLabels:
          app: postgres
    ports:
    - protocol: TCP
      port: 5432
  - to:
    - podSelector:
        matchLabels:
          app: redis
    ports:
    - protocol: TCP
      port: 6379
  - to:
    - namespaceSelector: {}
      podSelector:
        matchLabels:
          k8s-app: kube-dns
    ports:
    - protocol: UDP
      port: 53
---
# 允许 Simulator 访问 Gateway 和 Mosquitto
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: allow-simulator-egress
  namespace: iothunter
spec:
  podSelector:
    matchLabels:
      app: device-simulator
  policyTypes:
  - Egress
  egress:
  - to:
    - podSelector:
        matchLabels:
          app: iot-gateway
    ports:
    - protocol: TCP
      port: 80
  - to:
    - podSelector:
        matchLabels:
          app: mosquitto
    ports:
    - protocol: TCP
      port: 1883
    - protocol: TCP
      port: 8083
  - to:
    - namespaceSelector: {}
      podSelector:
        matchLabels:
          k8s-app: kube-dns
    ports:
    - protocol: UDP
      port: 53
---
# 【Ingress】允许入站到 IoTGateway（否则 default-deny-all 会拒绝 Simulator / 监控抓取）
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: allow-gateway-ingress
  namespace: iothunter
spec:
  podSelector:
    matchLabels:
      app: iot-gateway
  policyTypes:
  - Ingress
  ingress:
  - from:
    - podSelector:
        matchLabels:
          app: device-simulator
    ports:
    - protocol: TCP
      port: 80
    - protocol: TCP
      port: 9464
  - from:
    - podSelector:
        matchLabels:
          app: prometheus
    ports:
    - protocol: TCP
      port: 80
    - protocol: TCP
      port: 9464
  - from:
    - podSelector:
        matchLabels:
          app: iothunter-management
    ports:
    - protocol: TCP
      port: 80
    - protocol: TCP
      port: 9464
---
# 【Ingress】允许入站到 BackendProcessor（Simulator 查 API、Prometheus 抓取指标等）
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: allow-processor-ingress
  namespace: iothunter
spec:
  podSelector:
    matchLabels:
      app: backend-processor
  policyTypes:
  - Ingress
  ingress:
  - from:
    - podSelector:
        matchLabels:
          app: device-simulator
    ports:
    - protocol: TCP
      port: 80
    - protocol: TCP
      port: 9464
  - from:
    - podSelector:
        matchLabels:
          app: prometheus
    ports:
    - protocol: TCP
      port: 80
    - protocol: TCP
      port: 9464
  - from:
    - podSelector:
        matchLabels:
          app: iothunter-webui
    ports:
    - protocol: TCP
      port: 80
    - protocol: TCP
      port: 9464
---
# 【Ingress】allow-middleware-ingress：放行 iot-gateway / backend-processor / device-simulator → kafka / postgres / redis / mosquitto
#（单条策略通过 podSelector.matchExpressions 聚合中间件 Pod；各组件仅监听各自端口，多余端口声明不影响安全边界）
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: allow-middleware-ingress
  namespace: iothunter
spec:
  podSelector:
    matchExpressions:
      - key: app
        operator: In
        values:
          - kafka
          - postgres
          - redis
          - mosquitto
  policyTypes:
    - Ingress
  ingress:
    - from:
        - podSelector:
            matchExpressions:
              - key: app
                operator: In
                values:
                  - iot-gateway
                  - backend-processor
                  - device-simulator
      ports:
        - protocol: TCP
          port: 9092
        - protocol: TCP
          port: 5432
        - protocol: TCP
          port: 6379
        - protocol: TCP
          port: 1883
        - protocol: TCP
          port: 8083
---
# 【P-F1 / Egress】WebUI → Management / BackendProcessor / DeviceSimulator + DNS
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: allow-webui-egress
  namespace: iothunter
spec:
  podSelector:
    matchLabels:
      app: iothunter-webui
  policyTypes:
    - Egress
  egress:
    - to:
        - podSelector:
            matchLabels:
              app: iothunter-management
      ports:
        - protocol: TCP
          port: 80
    - to:
        - podSelector:
            matchLabels:
              app: backend-processor
      ports:
        - protocol: TCP
          port: 80
        - protocol: TCP
          port: 9464
    - to:
        - podSelector:
            matchLabels:
              app: device-simulator
      ports:
        - protocol: TCP
          port: 80
        - protocol: TCP
          port: 9464
    - to:
        - namespaceSelector: {}
          podSelector:
            matchLabels:
              k8s-app: kube-dns
      ports:
        - protocol: UDP
          port: 53
        - protocol: TCP
          port: 53
---
# 【P-F1 / Ingress】Management 仅接受来自 WebUI 的 HTTP
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: allow-webui-to-management
  namespace: iothunter
spec:
  podSelector:
    matchLabels:
      app: iothunter-management
  policyTypes:
    - Ingress
  ingress:
    - from:
        - podSelector:
            matchLabels:
              app: iothunter-webui
      ports:
        - protocol: TCP
          port: 80
---
# 【P-F1 / Ingress】BackendProcessor + DeviceSimulator 接受来自 WebUI 的 API / 指标
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: allow-webui-to-backends
  namespace: iothunter
spec:
  podSelector:
    matchExpressions:
      - key: app
        operator: In
        values:
          - backend-processor
          - device-simulator
  policyTypes:
    - Ingress
  ingress:
    - from:
        - podSelector:
            matchLabels:
              app: iothunter-webui
      ports:
        - protocol: TCP
          port: 80
        - protocol: TCP
          port: 9464
---
# 【Egress】第 9 步 Management → IoTGateway（`/api/v1/config`，满足 P-F2）
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: allow-management-egress-to-gateway
  namespace: iothunter
spec:
  podSelector:
    matchLabels:
      app: iothunter-management
  policyTypes:
  - Egress
  egress:
  - to:
    - podSelector:
        matchLabels:
          app: iot-gateway
    ports:
    - protocol: TCP
      port: 80
  - to:
    - namespaceSelector: {}
      podSelector:
        matchLabels:
          k8s-app: kube-dns
    ports:
    - protocol: UDP
      port: 53
# 说明：本节已含「三条」核心业务入站：`allow-gateway-ingress`、`allow-processor-ingress`、`allow-middleware-ingress`（P-A1-4）；
# 以及 WebUI：`allow-webui-egress`、`allow-webui-to-management`、`allow-webui-to-backends`（P-F1）。
# WebUI / Management Deployment 须在 Pod `metadata.labels.app` 上分别使用 `iothunter-webui`、`iothunter-management`。
# default-deny-all 为命名空间级；若 Prometheus 等组件在其它命名空间，请在对应 `Ingress`/`Egress` 的 `podSelector.matchLabels` / `namespaceSelector` 上联调。
```

执行：

```bash
kubectl apply -f deploy/network-policy.yaml
```

**验证**：从临时 Pod 尝试访问 Kafka 应失败，从 Simulator Pod 访问 Gateway 应成功。  
```bash
# 创建临时 Pod
kubectl run test-pod --rm -it --image=alpine -- sh
# 在 Pod 内执行
wget -qO- --timeout=3 http://iot-gateway.iothunter.svc/health/ready
# 预期：超时（NetworkPolicy 拒绝）
exit
```
**☐ 通过。**

---

### 1.9 滚动重启验证与优雅停机

**操作**：执行 `kubectl rollout restart deployment/iot-gateway -n iothunter` 并观察 Simulator 日志是否有假成功。

**验证**：
```bash
# 持续发送请求
kubectl exec -it deploy/device-simulator -n iothunter -- sh -c "while true; do curl -s -X POST http://iot-gateway/api/v1/telemetry -H 'Content-Type: application/json' -d '{\"deviceId\":\"restart-test\",\"metricType\":\"t\",\"timestamp\":1717000000000,\"sequence\":1}'; sleep 0.2; done" &
# 触发滚动重启
kubectl rollout restart deployment/iot-gateway -n iothunter
# 观察输出：重启期间应出现 503，无 202 假成功
# 重启完成后 Kafka Lag 应迅速归零
```

**☐ 通过。**

---

## 2. Live E2E 验证剧本（V8.0 增强版）

> **前置条件**：K3s 集群就绪，所有 Deployment 已应用。

| 编号 | 验证场景           | 操作                                                         | 预期结果                                                     | 通过 |
| ---- | ------------------ | ------------------------------------------------------------ | ------------------------------------------------------------ | ---- |
| 6.1  | 所有 Pod Running   | `kubectl get pods -n iothunter`                              | IoTGateway(2)、BackendProcessor(1)、DeviceSimulator(1)、Mosquitto(1)、Kafka(1)、PG(1)、Redis(1)、TimescaleDB(1) 均为 Running | ☐    |
| 6.2  | Gateway 健康检查   | 进入 Simulator Pod 执行 `curl http://iot-gateway/health/ready` | Healthy，kafka和mqtt均Healthy                                | ☐    |
| 6.3  | HTTP 遥测全链路    | 从 Simulator Pod 发送 HTTP 请求，查 PG                       | 数据落库                                                     | ☐    |
| 6.4  | MQTT 遥测          | 使用 Secret 中的凭证连接 Mosquitto 并发送消息                | Kafka 可见                                                   | ☐    |
| 6.5  | NetworkPolicy 生效 | 从临时 Pod 尝试访问 `kafka:9092`                             | 连接超时或被拒绝                                             | ☐    |
| 6.6  | TLS 访问 Gateway   | `curl -k https://iot-gateway.iothunter.svc/health/ready`     | Healthy                                                      | ☐    |
| 6.7  | 滚动重启无假成功   | `kubectl rollout restart deployment/iot-gateway`             | 重启期间无 202 假成功，503 返回                              | ☐    |
| 6.8  | 安全上下文非 root  | `kubectl exec deploy/iot-gateway -n iothunter -- whoami`     | 非 root 用户（如 `app` 或 `1000`）                           | ☐    |
| 6.9  | Secret 验证        | `kubectl describe secret mosquitto-config -n iothunter`      | 包含三个文件条目，无明文泄露                                 | ☐    |
| 6.10 | MQTT 凭证独立验证  | `kubectl get secret mqtt-credentials -n iothunter -o jsonpath='{.data.gateway-password}' \| base64 -d` | 输出与创建 Secret 时为 `gateway-password` 注入的值一致 | ☐    |

---

## 3. 人工测试操作指南

### 3.1 环境确认

- `kubectl config current-context` 指向 K3s 集群
- `kubectl get nodes` 显示 Ready
- 中途件容器已部署或本步部署

### 3.2 部署验证

- 按照 1.1-1.9 逐条执行，每步使用 `kubectl get` 或 `kubectl logs` 确认
- 使用两个终端窗口：一个执行命令，一个 `kubectl get pods -n iothunter -w` 观察变化

### 3.3 逐项测试

#### 测试 6.1：所有 Pod Running

```bash
kubectl get pods -n iothunter
```

**预期**：8 个服务全部 Running。  
**☐ 通过。**

#### 测试 6.2：Gateway 健康检查

```bash
kubectl exec -it deploy/device-simulator -n iothunter -- curl -s http://iot-gateway/health/ready
```

**预期**：`{"status":"Healthy","results":{"kafka":{"status":"Healthy"},"mqtt":{"status":"Healthy"}}}`  
**☐ 通过。**

#### 测试 6.3：HTTP 遥测全链路

```bash
kubectl exec -it deploy/device-simulator -n iothunter -- curl -s -X POST http://iot-gateway/api/v1/telemetry -H "Content-Type: application/json" -d '{"deviceId":"k8s-test","metricType":"t","timestamp":1717000000000,"sequence":1}'
sleep 3
kubectl exec -it deploy/postgres -n iothunter -- psql -U iotapp -d IoTHunter -c "SELECT COUNT(*) FROM telemetry_records WHERE device_id='k8s-test';"
```

**预期**：COUNT 为 1。  
**☐ 通过。**

#### 测试 6.5：NetworkPolicy 生效

```bash
kubectl run test-pod --rm -it --image=alpine -n iothunter -- sh
# 在 Pod 内执行
wget -qO- --timeout=3 http://kafka.iothunter.svc:9092
# 预期：超时（NetworkPolicy 拒绝）
exit
```

**☐ 通过。**

#### 测试 6.8：安全上下文非 root

```bash
kubectl exec -it deploy/iot-gateway -n iothunter -- whoami
```

**预期**：输出非 root，如 `app` 或 `1000`。  
**☐ 通过。**

#### 测试 6.9：Secret 验证

```bash
kubectl describe secret mosquitto-config -n iothunter
```

**预期**：Data 部分列出的条目不包含明文密码，均为经过第5步加密的 Hash。  
**☐ 通过。**

### 3.4 收尾

- 将所有验证输出保存至 `screenshots/step6/`。

---

## 4. 完成标准

- [ ] 所有服务在 `iothunter` 命名空间 Running
- [ ] NetworkPolicy 最小权限生效（非法 Pod 无法访问中间件）
- [ ] TLS 证书已部署，Gateway 可通过 HTTPS 访问
- [ ] 滚动重启 Gateway 无假成功
- [ ] **安全上下文满足非 root、只读文件系统（readOnlyRootFilesystem: true）**
- [ ] **Mosquitto Secret 通过命令式注入，无明文 YAML 残留**
- [ ] **MQTT 凭证已分离为独立 Secret**
- [ ] Live 验证 10 项全部勾选通过
- [ ] 无硬编码连接串或明文密码在 YAML 中

---

第六步开发计划 V8.0 最终版完毕。Secret 已四条 `kubectl create` 命令式注入（P6-02），MQTT 凭证已分离，安全上下文与 `imagePullPolicy: IfNotPresent` 已写入应用清单，IoTGateway `limits.memory` 为 512Mi（P6-06），NetworkPolicy 含 default-deny、出站/入站白名单、`allow-middleware-ingress`（P-A1-4）及 WebUI `allow-webui-*`（P-F1），Mosquitto subPath 挂载已落实。请定稿。