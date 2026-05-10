# 第七步 人工测试操作指南

## 环境确认
```bash
kubectl get pods -n iothunter | grep -E "otel|jaeger|prometheus|grafana"
```
预期：otel-collector、jaeger、prometheus、grafana 四个 Pod Running。

## 端口转发
打开三个终端：
```bash
# Jaeger
kubectl port-forward -n iothunter svc/jaeger-query 16686:16686
# Grafana
kubectl port-forward -n iothunter svc/grafana 3000:3000
# Prometheus
kubectl port-forward -n iothunter svc/prometheus 9090:9090
```

## Prometheus 指标验证
执行修复 1 中的四条 curl 命令，确保 `gateway_requests_total`、`gateway_kafka_ack_latency_ms_bucket`、`mqtt_messages_total`、`gateway_rejected_total` 均返回 success。

## Jaeger 全链路追踪验证
1. 发送测试数据
```bash
kubectl exec deploy/device-simulator -n iothunter -- curl -s -X POST http://iot-gateway/api/v1/telemetry \
  -H "Content-Type: application/json" \
  -d "{\"deviceId\":\"trace-demo-001\",\"metricType\":\"heart_rate\",\"timestamp\":$(date +%s)000,\"sequence\":1}"
```
2. 浏览器打开 `http://localhost:16686`，Service 选择 `IoTGateway`，点击 Find Traces。
3. 预期看到完整 Span 树（HTTP/POST → Kafka.Produce → BackendProcessor）。

## Grafana 仪表板验证
按修复 2 导入仪表板 19924，并创建 IoTHunter Gateway Overview 面板，确认 QPS 曲线、P99 延迟、MQTT 计数面板有数据。

## TraceController 验证
按修复 3 执行 curl，预期返回 HTTP 200。

## 日志关联验证
```bash
TRACE_ID=$(kubectl logs -n iothunter deploy/iot-gateway --tail=50 | grep -oP 'trace=\K[0-9a-f]{32}' | head -1)
echo $TRACE_ID
# 在 Jaeger UI 搜索该 TraceID，确认定位到同一 Trace
```

## 收尾
截图保存至 `screenshots/step7/`。
