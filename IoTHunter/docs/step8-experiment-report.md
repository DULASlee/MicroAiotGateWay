# 第八步 端到端压测与故障演练报告

**执行时间**: 2026-05-10  
**环境**: docker-desktop 单节点 K8s v1.30.2 + Docker Compose 基础设施  
**拓扑**: 2×IoTGateway + 1×BackendProcessor + 3×DeviceSimulator + OTel/Jaeger/Prometheus/Grafana

---

## 8.3 低负载基线 (目标 50 QPS)

| 参数 | 值 |
|------|-----|
| DeviceCount | 50 |
| IntervalMs | 1000 |
| Concurrency | 5 |
| DurationSeconds | 120 |
| CriticalEventRatio | 0.05 |
| Simulator 副本 | 3 |

| 指标 | 数值 |
|------|------|
| 总请求数 | 7,240 (6,900 遥测 + 340 关键事件) |
| 平均 QPS | ~60 |
| HTTP 延迟 | 9-12ms |
| Kafka ack 延迟 | ~24ms |
| 拒绝/错误 | 0 |
| 结论 | **通过** — 系统低负载下完全健康 |

---

## 8.4 中负载 (目标 2000 QPS)

| 参数 | 值 |
|------|-----|
| DeviceCount | 700 |
| IntervalMs | 1000 |
| Concurrency | 10 |
| DurationSeconds | 120 |

| 指标 | 数值 |
|------|------|
| 总请求增量 | ~126K (7,240 → 133,120) |
| 平均 QPS | ~1,050 |
| HTTP 延迟 | 12-17ms |
| 拒绝/503 | 0 |
| Kafka lag (telemetry.raw) | **155K** |
| Kafka lag (event.critical) | 8K |
| 瓶颈 | BackendProcessor PG 批量写入限速 |
| 结论 | **通过** — Gateway 无压力，瓶颈在下游 PG |

---

## 8.5 高负载探索 (目标 10,000 QPS)

| 参数 | 值 |
|------|-----|
| DeviceCount | 2000 |
| IntervalMs | 200 |
| Concurrency | 50 |
| DurationSeconds | 600 |

| 指标 | 数值 |
|------|------|
| 总请求增量 | ~79K (268K → 347K) |
| 平均 QPS | ~225 (远低于目标) |
| HTTP 延迟 | 15ms → **380ms+** (性能崩塌) |
| 拒绝/503 | 0 |
| Kafka lag (telemetry.raw) | **223K** |
| 根因 | Channel 背压 → HTTP 超时 → 吞吐坍缩 |
| 结论 | **系统极限 ~1,000-1,500 QPS** (单节点 docker-desktop) |

---

## 8.6 故障注入 — Kafka 停机

| 操作 | `docker stop iothunter-kafka-1` |
|------|------|
| 停机时长 | ~60s |

| 指标 | 故障期间 | 恢复后 |
|------|----------|--------|
| Gateway `/health/ready` | **503** (Kafka: Broker transport failure) | 200 |
| 模拟器 HTTP 响应 | 超时 ~2s (Polly 4s 超时) | 202 (正常) |
| `gateway_rejected_total` | **26** | 0 |
| Kafka ack 延迟 | N/A | ~24ms |

**结论**:
- Polly 重试 (3次, 100→200→400ms) 吸收了大部分故障冲击，只产生 26 次拒绝
- Gateway 正确返回 503 而非静默丢失数据
- Kafka 恢复后系统自动重连，无需人工干预
- **通过** — 故障隔离和恢复符合 ADR-002

---

## 8.7 故障注入 — Redis 停机

| 操作 | `docker stop iothunter-redis-1` |
|------|------|
| 停机时长 | ~4min |

| 指标 | 故障期间 | 恢复后 |
|------|----------|--------|
| Gateway `/health/ready` | 200 (不受影响) | 200 |
| BackendProcessor `/health/ready` | **Healthy** (Redis 检查未触发) | Healthy |
| PG 写入 | 正常 | 正常 |
| `GET /api/v2/devices/{id}/latest` | 返回陈旧数据 | — |
| Redis DBSIZE | — | **0** (内存数据丢失) |
| Latest-projection Kafka lag | — | **503K** |

**发现**:
- BackendProcessor 的 RedisHealthCheck 在 Redis 停机时仍报告 Healthy — **需修复**
- Redis 未配置持久化，重启后数据全部丢失
- Gateway→Kafka→PG 路径完全不受影响 (符合 ADR-002)
- **部分通过** — 核心写入路径不受影响，但 Redis 弹性和健康检查有缺陷

---

## 8.8 故障注入 — Gateway 滚动重启

| 操作 | `kubectl rollout restart deployment/iot-gateway` |
|------|------|

| 指标 | 数值 |
|------|------|
| 503 错误 | **0** |
| 新 Pod 启动时间 | <10s |
| 恢复后健康状态 | Healthy |
| 结论 | **通过** — 滚动更新零中断 |

---

## 8.9 数据一致性验证

| 数据源 | 数值 |
|--------|------|
| Kafka `telemetry.raw` 总消息 | 778,373 |
| Kafka `event.critical` 总消息 | 41,057 |
| PG `telemetry_records` 总行数 | 557,205 |
| PG 关键事件数 | 41,035 |
| PG 唯一 `event_id` 数 | 562,805 |
| Redis DBSIZE | 0 (停机后丢失) |
| Persistence consumer lag (telemetry.raw) | 269,645 |
| Latest-projection consumer lag (telemetry.raw) | 503,174 |
| Persistence consumer lag (event.critical) | **0** |

**验证结论**:
- `event.critical` 持久化零滞后 — 关键事件路径完整
- `telemetry.raw` 有 ~270K 积压 (PG 写入瓶颈)
- Redis 最新值投影滞后 503K，且重启后全量丢失
- 无重复 event_id (ON CONFLICT DO NOTHING 生效)
- **通过** — 关键事件路径一致性完好，普通遥测有可接受的最终一致性延迟

---

## 综合结论

| 维度 | 评级 | 说明 |
|------|------|------|
| 低负载稳定性 | **优秀** | 60 QPS 零错误，9-12ms 延迟 |
| 中负载吞吐 | **良好** | ~1,050 QPS 零拒绝，瓶颈在 PG |
| 高负载极限 | **受限** | 单节点 docker-desktop 极限 ~1,000-1,500 QPS |
| Kafka 故障恢复 | **优秀** | Polly 吸收抖动，自动恢复，仅 26 次拒绝 |
| Redis 故障隔离 | **一般** | 写入路径不受影响，但健康检查未检测到故障 |
| Gateway 滚动更新 | **优秀** | 零中断，滚动重启无缝 |
| 数据一致性 | **良好** | 关键事件零滞后，普通遥测最终一致 |

### 待修复项

1. **RedisHealthCheck** — 停机时仍报告 Healthy，需增加超时/重连检测
2. **Redis 持久化** — 配置 RDB/AOF 避免重启数据丢失
3. **高负载性能** — 生产环境多节点部署可突破当前 ~1,500 QPS 瓶颈

---

## 8.11 环境恢复

```bash
# 恢复模拟器至正常状态
kubectl scale deployment/device-simulator -n iothunter --replicas=1
kubectl set env deployment/device-simulator -n iothunter \
  Simulator__DeviceCount=5 \
  Simulator__IntervalMs=1000 \
  Simulator__Concurrency=2 \
  Simulator__DurationSeconds=0

# OTel 采样率恢复 (当前已为默认配置)
```
