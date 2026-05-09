# 第 8 步开发计划：端到端压测、故障演练与实验报告（Live E2E 增强版 V8.0 最终版）

> **版本**：V8.0（熔接三次裁决：压测参数重算、配置模型统一、Simulator 注入方式明确、实验报告模板、OTel 采样率恢复）  
> **对应总架构**：`架构设计文档 V7.0` 第 8 步  
> **前置依赖**：第 7 步全部验证通过，可观测性栈正常运行，Jaeger 可查看完整 Trace，Grafana 仪表板可用，Simulator 已在 K3s 中部署  
> **本步目标**：执行混合协议高并发压测，验证接入可靠性（2000+ QPS）、分级韧性（MQTT QoS 1 / HTTP Kafka Ack）、投影最终一致性（Redis/时序）和安全基线，产出可放入简历的完整实验报告。**所有压测操作通过 `kubectl set env` 注入参数，不修改代码。**  
> **本步边界**：不修改任何服务代码、Dockerfile 或 K8s 清单，仅通过配置和 `kubectl` 命令驱动压测、故障注入与数据核验。  
> **核心禁令**：所有验证在真实集群进行，禁止模拟。  
> **【V8.0 关键修正】**：  
> - **压测参数统一（P8-01）**：与仓库 `SimulatorOptions` 一致，仅 **`IntervalMs`** / **`Concurrency`**；理论 **QPS = DeviceCount × (1000 / IntervalMs)**。**禁止** MessagesPerSecond、HttpConnections（及已无文档支持的 WarmupSeconds、Scenario）。  
> - **阶梯加压数值重算**：低负载(50 设备, IntervalMs=1000 → 50 QPS)、中负载(200 设备, IntervalMs=100 → 2000 QPS)、高负载(500 设备, IntervalMs=50 → 10000 QPS)。  
> - **Simulator 配置注入**：使用 `kubectl set env` 动态调整压测参数，无需重启 Pod。  
> - **实验报告模板**：提供 Markdown 模板，明确必须包含的章节。  
> - **OTel 采样率恢复**：压测结束后将采样率从 100% 恢复为 5%，防止 Jaeger 内存耗尽。

---

## 0. 前置检查

| 检查项           | 命令                                                         | 状态要求                                                     |
| ---------------- | ------------------------------------------------------------ | ------------------------------------------------------------ |
| 所有 Pod Running | `kubectl get pods -n iothunter`                              | IoTGateway（2副本）、BackendProcessor、DeviceSimulator、Mosquitto、Kafka、PostgreSQL、Redis、TimescaleDB、Jaeger、Prometheus、Grafana 等均为 Running |
| 健康检查全绿     | `kubectl exec -n iothunter deploy/iot-gateway -- curl -s http://localhost/health/ready` | Healthy                                                      |
| Grafana 可访问   | 端口转发后能查看仪表板，QPS 面板有实时数据                   | —                                                            |
| Jaeger 可访问    | 端口转发后能查询 Trace，服务列表含 `IoTGateway`              | —                                                            |
| Simulator 已部署 | `kubectl get pods -n iothunter -l app=device-simulator`      | Running                                                      |
| 第 7 步验证通过  | `screenshots/step7/` 下全部证据                              | 7 项已勾选                                                   |

---

## 1. 动作项

### 1.1 压测准备与基线建立

#### 1.1.1 确认 OTel 采样率为 100%

第 7 步已默认配置 100% 采样率，无需额外操作。可通过以下命令确认：

```bash
kubectl exec -n iothunter -l app=opentelemetry-collector -- cat /etc/otel/config.yaml | grep sampling_percentage
# 预期输出: sampling_percentage: 100.0
```

#### 1.1.2 扩容 Simulator 副本数

```bash
kubectl scale deployment device-simulator -n iothunter --replicas=3
kubectl rollout status deployment/device-simulator -n iothunter
```

#### 1.1.3 初始化压测参数（低负载基线）

**【V8.0 关键】使用 `kubectl set env` 注入压测参数，与仓库 `SimulatorOptions` 字段名一致**：

```bash
kubectl set env deployment/device-simulator -n iothunter \
  Simulator__Protocol=http \
  Simulator__DeviceCount=50 \
  Simulator__IntervalMs=1000 \
  Simulator__Concurrency=5 \
  Simulator__DurationSeconds=120 \
  Simulator__CriticalEventRatio=0.05
```

**【重要说明】**：
- `IntervalMs` / `Concurrency` 对应仓库 `SimulatorOptions.IntervalMs`、`SimulatorOptions.Concurrency`
- QPS 理论值 = **DeviceCount × (1000 / IntervalMs)**
- 以上低负载配置：50 × (1000 / 1000) = **50 QPS**

#### 1.1.4 配置 Grafana 面板

打开 Grafana，确认以下面板已就绪并实时刷新：
- `Gateway QPS by Protocol`
- `Kafka Ack Latency P99`
- `MQTT Message Count by QoS`
- 时间范围设为 `Last 15 minutes`，刷新间隔 `5s`

同时打开 Jaeger UI，备用于链路验证。

**验证**：Grafana 面板显示当前 QPS 约 50，延迟稳定。☐

---

### 1.2 渐进式加压与吞吐量记录（V8.0：压测参数重算）

**【P8-01】理论网关侧等价 QPS（条/秒）**：**DeviceCount × (1000 / IntervalMs)**（要求 IntervalMs > 0；与每台设备每秒约发送 `1000/IntervalMs` 条等价）。下文阶梯加压表与各轮 `kubectl set env` **均基于此公式**，且 **只可**使用 `Simulator__IntervalMs`、`Simulator__Concurrency`，**禁止** `Simulator__MessagesPerSecond`、`Simulator__HttpConnections`。

**【V8.0 关键修正】三轮加压参数如下：**

| 轮次   | DeviceCount | IntervalMs (ms/条·设备) | Concurrency | 理论 QPS | 持续时间 | 验证目标         |
| ------ | ----------- | ------------------------ | ----------- | -------- | -------- | ---------------- |
| 低负载 | 50          | 1000（约 1 条/秒·设备）  | 5           | 50       | 2 分钟   | 系统基本稳定     |
| 中负载 | 200         | 100（约 10 条/秒·设备） | 20          | **2000** | 3 分钟   | **达到目标 QPS** |
| 高负载 | 500         | 50（约 20 条/秒·设备）  | 50          | 10000    | 3 分钟   | 找到性能拐点     |

#### 1.2.1 低负载（50 QPS）

确认 1.1.3 配置已生效，观察 Grafana 2 分钟，记录：
- 实际 QPS
- P99 延迟（Kafka ack 口径）
- Kafka Consumer Lag
- PG 写入 TPS（通过 `kubectl exec` 查询）

#### 1.2.2 中负载（目标 2000 QPS）（**P8-01**：`IntervalMs=100`、`Concurrency=20`）

```bash
kubectl set env deployment/device-simulator -n iothunter \
  Simulator__DeviceCount=200 \
  Simulator__IntervalMs=100 \
  Simulator__Concurrency=20 \
  Simulator__DurationSeconds=180
kubectl rollout status deployment/device-simulator -n iothunter
```

Simulator 自动重启并以新参数运行。观察 Grafana 3 分钟，记录：
- 实际 QPS（应接近 2000）
- P99 延迟
- Kafka Consumer Lag（不应持续增长）
- PG 写入 TPS

**预期**：QPS ≥ 2000，P99 ≤ 100ms，Lag 在压测结束后 30 秒内归零。

#### 1.2.3 高负载（探索极限）（**P8-01**：`IntervalMs=50`、`Concurrency=50`）

```bash
kubectl set env deployment/device-simulator -n iothunter \
  Simulator__DeviceCount=500 \
  Simulator__IntervalMs=50 \
  Simulator__Concurrency=50 \
  Simulator__DurationSeconds=180
kubectl rollout status deployment/device-simulator -n iothunter
```

观察 Grafana 3 分钟，记录系统在高负载下的表现，找到可能的性能瓶颈（CPU/内存/网络）。

**验证**：三轮压测数据已记录在实验报告中，中负载达到 2000+ QPS。☐

---

### 1.3 故障注入与韧性验证

**执行顺序**：每种故障独立执行，恢复后再进行下一项。

#### 故障一：删除 Kafka Pod（验证接入降级）

```bash
kubectl delete pod -n iothunter -l app=kafka
```

**观察**：
- Grafana QPS 面板显示请求开始返回 503 状态码
- Gateway 日志出现 "Kafka produce retry"
- Simulator 日志显示 HTTP 503 错误
- **无假成功**（202 响应在 Kafka 恢复前不应出现）

**等待 Kafka Pod 自动重启并 Ready**：
```bash
kubectl wait --for=condition=ready pod -l app=kafka -n iothunter --timeout=120s
```

**验证恢复**：Kafka 恢复后 30 秒内，Grafana 面板显示请求恢复 202，Kafka Lag 迅速归零。

**验收标准**：故障期间 Gateway 返回 503，不产生假成功；恢复后自愈。☐

#### 故障二：删除 Redis Pod（验证投影隔离）

```bash
kubectl delete pod -n iothunter -l app=redis
```

**观察**：
- PG 写入不受影响（Grafana 面板持续显示写入 TPS）
- `/latest` 查询可能短暂返回 404（Redis 故障期间无投影数据）
- BackendProcessor 日志出现 Redis 连接错误，但不崩溃

**等待 Redis Pod 自动重启并 Ready**：
```bash
kubectl wait --for=condition=ready pod -l app=redis -n iothunter --timeout=60s
```

**验证恢复**：Redis 恢复后，`/latest` 查询重新返回数据，投影追平。

**验收标准**：Redis 故障不阻塞 PG 写入；恢复后投影追平。☐

#### 故障三：删除 Gateway Pod（验证滚动更新安全）

```bash
kubectl delete pod -n iothunter -l app=iot-gateway
```

**观察**：
- 由于有 2 个副本，Service 自动切换到健康 Pod
- 滚动重启期间短暂 QPS 下降，但无 503 返回（另一个副本仍在服务）
- 新 Pod 启动后，MQTT 连接自动恢复

**验收标准**：滚动重启期间不产生假成功；MQTT 消息可恢复投递。☐

#### 故障四：时序存储故障（可选，若已部署 TimescaleDB）

```bash
kubectl delete pod -n iothunter -l app=timescaledb
```

**验收标准**：主链路（PG 业务库）不阻塞，时序查询暂时不可用，恢复后数据追平。☐

---

### 1.4 数据一致性最终核验

压测结束后，等待 30 秒确保所有 Kafka 消息已被消费：

```bash
# 检查 Kafka Lag 归零
kubectl exec -n iothunter deploy/kafka -- kafka-consumer-groups \
  --bootstrap-server localhost:9092 \
  --group iot-persistence --describe

sleep 30
```

#### 1.4.1 Redis 最新值核验

```bash
kubectl exec -n iothunter deploy/redis -- redis-cli HGETALL device:latest:dev-000001
```

**预期**：`sequence` 字段为设备 `dev-000001` 的当前最大值。

#### 1.4.2 PostgreSQL 主事实库核验

```bash
kubectl exec -n iothunter deploy/postgres -- psql -U iotapp -d IoTHunter -c "
SELECT COUNT(DISTINCT event_id) AS total_unique, COUNT(*) AS total_rows
FROM telemetry_records;"
```

**预期**：`total_unique == total_rows`（幂等去重生效，无重复记录）。

#### 1.4.3 关键事件独立核验

```bash
# Kafka event.critical 的 Offset
kubectl exec -n iothunter deploy/kafka -- kafka-run-class kafka.tools.GetOffsetShell \
  --broker-list localhost:9092 --topic event.critical --time -1

# PG 中标记为关键事件的记录数
kubectl exec -n iothunter deploy/postgres -- psql -U iotapp -d IoTHunter -t -c "
SELECT COUNT(DISTINCT event_id) FROM telemetry_records WHERE reliability=2;"
```

**预期**：PG 中 `reliability=2` 的记录数与 Kafka `event.critical` 的 Offset 数量完全一致。

#### 1.4.4 查询 API 验证

```bash
kubectl exec -n iothunter deploy/device-simulator -- curl -s http://backend-processor/api/v2/devices/dev-000001/latest
kubectl exec -n iothunter deploy/device-simulator -- curl -s "http://backend-processor/api/v2/devices/dev-000001/history?page=1&pageSize=5"
```

**预期**：返回正确 JSON 数据，`/latest` 含最新值，`/history` 含分页数据，`/latest` 无数据时返回 404。

**验证**：所有核验结果已截图保存。☐

---

### 1.5 全链路 Trace 验证与报告生成（V8.0：实验报告模板）

#### 1.5.1 选取完整 Trace

1. 打开 Jaeger UI，选择 `IoTGateway` 服务。
2. 搜索最近的一条 Trace（由于 100% 采样，必定存在）。
3. 展开 Span 树，确认结构完整：
   ```
   DeviceSimulator → IoTGateway (HTTP/POST) → IoTGateway (Kafka.Produce)
   → BackendProcessor (Kafka.Consume) → BackendProcessor (BatchWriteToPG)
   ```
4. 截图保存为 `screenshots/step8/trace-full-link.png`。

#### 1.5.2 日志关联验证

```bash
# 从 Gateway 日志提取 traceId
TRACE_ID=$(kubectl logs -n iothunter deploy/iot-gateway --tail=100 | grep -oP 'trace=\K[0-9a-f]{32}' | head -1)
echo "Trace ID: $TRACE_ID"
# 在 Jaeger UI 搜索该 ID，确认返回同一 Trace
# 在 Processor 日志中搜索同一 ID
kubectl logs -n iothunter deploy/backend-processor --tail=200 | grep "$TRACE_ID"
```

**预期**：Gateway 和 Processor 日志中出现相同的 `trace_id`。

#### 1.5.3 实验报告生成（V8.0：Markdown 模板）

创建 `docs/experiment-report.md`，按以下模板编写。**此模板为强制性结构，不可自由发挥**：

```markdown
# IoTHunter 端到端压测实验报告

> **执行日期**：YYYY-MM-DD  
> **环境**：K3s 集群，X 节点，每节点 Y CPU / Z GB 内存  
> **版本**：IoTHunter V8.0

## 1. 测试环境

| 组件 | 版本 | 部署方式 |
|------|------|----------|
| IoTGateway | V8.0 | K3s Deployment, 2 副本 |
| BackendProcessor | V8.0 | K3s Deployment, 1 副本 |
| DeviceSimulator | V8.0 | K3s Deployment, 3 副本 |
| Kafka | 7.6.0 | 单节点 |
| PostgreSQL | 16-alpine | 单节点 |
| Redis | 7-alpine | 单节点 |

## 2. 测试场景

| 场景 | 协议 | 设备数 | IntervalMs (ms) | 理论 QPS（DeviceCount × (1000 / IntervalMs)） | 持续时间 | Concurrency |
|------|------|--------|-----------------|------------------------------------------|----------|-------------|
| 低负载 | HTTP | 50 | 1000 | 50 | 120s | 5 |
| 中负载 | HTTP | 200 | 100 | 2000 | 180s | 20 |
| 高负载 | HTTP | 500 | 50 | 10000 | 180s | 50 |

## 3. 接入层性能

| 轮次 | 实际 QPS | P50 延迟 | P99 延迟 | P999 延迟 | 成功率 |
|------|----------|----------|----------|-----------|--------|
| 低负载 | (填入) | (填入) | (填入) | (填入) | (填入) |
| 中负载 | (填入) | (填入) | (填入) | (填入) | (填入) |
| 高负载 | (填入) | (填入) | (填入) | (填入) | (填入) |

## 4. Kafka 层指标

| 轮次 | 积压峰值 | 平均积压 | 最终积压 | 恢复时间 |
|------|----------|----------|----------|----------|
| 低负载 | (填入) | (填入) | 0 | N/A |
| 中负载 | (填入) | (填入) | 0 | N/A |
| 高负载 | (填入) | (填入) | 0 | N/A |

## 5. 存储层指标

| 轮次 | PG 写入 TPS 峰值 | PG 写入 TPS 平均 | PG 写入 P99 延迟 |
|------|-------------------|-------------------|-------------------|
| 低负载 | (填入) | (填入) | (填入) |
| 中负载 | (填入) | (填入) | (填入) |
| 高负载 | (填入) | (填入) | (填入) |

## 6. 故障注入记录

| 故障类型 | 注入时间 | 系统行为 | 恢复时间 | 数据影响 |
|----------|----------|----------|----------|----------|
| Kafka Pod 删除 | (填入) | Gateway 返回 503，无假成功 | (填入) | 无数据丢失 |
| Redis Pod 删除 | (填入) | PG 写入正常，/latest 短暂 404 | (填入) | 投影恢复后追平 |
| Gateway Pod 删除 | (填入) | 另一副本接替，MQTT 恢复投递 | (填入) | 无数据丢失 |

## 7. 数据一致性核验

| 核验项 | 结果 |
|--------|------|
| PG 唯一 event_id 数 = 总行数 | ✅/❌ |
| Simulator 成功量 ≈ PG 记录数（偏差<2%） | ✅/❌ |
| 关键事件 PG 记录 = Kafka event.critical Offset | ✅/❌ |
| Redis latest 值正确 | ✅/❌ |

## 8. 全链路追踪证据

- Jaeger Trace ID：(填入)
- Span 树完整截图路径：`screenshots/step8/trace-full-link.png`

## 9. 结论与优化建议

（根据实际测试结果总结系统表现，提出优化方向）
```

**要求**：报告正文不少于 1500 字，数值必须与 Grafana 面板和数据库查询结果一致，截图完整。

---

### 1.6 恢复 OTel 采样率为生产配置

压测和链路验证完成后，将 OTel Collector 采样率恢复为 5%：

```bash
# 修改 values.yaml 中的采样率
sed -i 's/sampling_percentage: 100.0/sampling_percentage: 5.0/' deploy/observability/otel-collector-values.yaml
# 升级 Helm release
helm upgrade otel-collector open-telemetry/opentelemetry-collector \
  -n iothunter \
  -f deploy/observability/otel-collector-values.yaml
# 重启 Collector
kubectl rollout restart daemonset otel-collector -n iothunter
```

**验证**：`kubectl exec -n iothunter -l app.kubernetes.io/name=opentelemetry-collector -- cat /etc/otel/config.yaml | grep sampling_percentage` 输出 `5.0`。☐

---

## 2. Live E2E 验证剧本（V8.0 增强版）

> **前置条件**：第7步正常运行，Simulator 已扩容，Grafana 和 Jaeger 可访问。

| 编号 | 验证场景             | 操作                                         | 预期结果                                | 通过 |
| ---- | -------------------- | -------------------------------------------- | --------------------------------------- | ---- |
| 8.1  | 低负载稳定           | 50 设备，IntervalMs=1000 → 约 50 QPS，运行 2 分钟           | Grafana QPS ≈ 50，成功率 > 99%          | ☐    |
| 8.2  | 中负载达标           | 200 设备，IntervalMs=100 → 约 2000 QPS，运行 3 分钟         | Grafana QPS ≥ 2000，P99 ≤ 100ms       | ☐    |
| 8.3  | 高负载不崩溃         | 500 设备，IntervalMs=50 → 约 10000 QPS，运行 3 分钟         | 系统不崩，成功率 > 90%                 | ☐    |
| 8.4  | Kafka 故障降级       | 删除 Kafka Pod                               | 503 响应，无假成功；自愈后恢复          | ☐    |
| 8.5  | Redis 故障隔离       | 删除 Redis Pod                               | PG 写入正常                             | ☐    |
| 8.6  | Gateway 滚动更新安全 | 删除 Gateway Pod                             | 无假成功，MQTT 恢复投递                 | ☐    |
| 8.7  | 数据一致性           | 查询 DB                                      | Simulator 成功量 ≈ PG 记录数（偏差<2%） | ☐    |
| 8.8  | 关键事件闭合         | 对比 Kafka Offset 与 PG                      | 数量一致                                | ☐    |
| 8.9  | 全链路 Trace 完整    | Jaeger 搜索                                  | 完整 Span 树存在                        | ☐    |
| 8.10 | 实验报告完整         | 查看 `docs/experiment-report.md`             | 所有章节有内容，≥1500 字                | ☐    |
| 8.11 | 采样率已恢复         | 检查 OTel 配置                               | `sampling_percentage: 5.0`              | ☐    |

---

## 3. 人工测试操作指南

### 3.1 环境确认

- 打开四个终端：
  - **终端 A**：执行 `kubectl` 命令
  - **终端 B**：Grafana 端口转发并实时观察
  - **终端 C**：Jaeger 端口转发
  - **终端 D**：监控 Pod 状态

```bash
# 终端 B
kubectl port-forward -n iothunter svc/prometheus-grafana 3000:80

# 终端 C
kubectl port-forward -n iothunter svc/jaeger-query 16686:16686

# 终端 D
kubectl get pods -n iothunter -w
```

### 3.2 执行三轮加压

1. 在终端 A 执行 `kubectl set env` 调整 Simulator 参数（参见 1.2 节）。
2. 观察终端 B（Grafana）的 QPS 面板，确认数值上升。
3. 每轮加压结束后，在 Grafana 截屏保存 `screenshots/step8/load-{low,mid,high}.png`。
4. 记录关键指标到实验报告模板。

**☐ 三轮加压完成，数据已记录。**

### 3.3 执行故障注入

1. **删除 Kafka Pod**（终端 A）：
   ```bash
   kubectl delete pod -n iothunter -l app=kafka
   ```
2. 观察终端 B（Grafana）：QPS 骤降，503 状态码出现。
3. 观察终端 C（Jaeger）：确认故障期间的请求没有产生 Trace。
4. 等待 Kafka Pod 自动恢复（`kubectl wait`），确认 QPS 恢复。
5. 重复类似步骤测试 Redis、Gateway 故障。

**☐ 四类故障测试完成，行为符合预期。**

### 3.4 数据一致性核验

1. 等待 Kafka Lag 归零（终端 A）：
   ```bash
   kubectl exec -n iothunter deploy/kafka -- kafka-consumer-groups \
     --bootstrap-server localhost:9092 --group iot-persistence --describe
   ```
2. 执行 SQL 核验（参见 1.4 节），截图保存。
3. 对比 Simulator Snapshot 与 PG 记录数。

**☐ 数据一致性核验完成。**

### 3.5 报告整理

1. 将上述截屏和日志归档到 `screenshots/step8/`。
2. 按 1.5.3 节模板填写 `docs/experiment-report.md`，确保所有数值与 Grafana/数据库一致。
3. 重点突出“降级存活”、“数据闭环”、“全链路追踪”三大亮点。

**☐ 报告已完成，不少于 1500 字。**

### 3.6 恢复采样率

按 1.6 节步骤恢复 OTel 采样率为 5%。

**☐ 采样率已恢复。**

---

## 4. 完成标准

- [ ] 混合协议 QPS ≥ 2000（中负载达标），P99 延迟 ≤ 100ms
- [ ] 4 类故障注入验证通过：Kafka/Redis/Gateway/时序存储（可选）
- [ ] 数据一致性偏差 < 2%，关键事件独立核验通过
- [ ] Jaeger 完整 Span 树存在，日志 traceId 可关联
- [ ] 实验报告按模板完成，所有章节有数据，≥1500 字
- [ ] S OTel 采样率已恢复为 5% 生产配置
- [ ] Live 验证 11 项全部勾选通过

---

第八步开发计划 V8.0 最终版完毕。D爷，压测参数已重算，配置模型已统一，Simulator 注入方式已明确，实验报告模板已提供，采样率恢复步骤已补全。请定稿。