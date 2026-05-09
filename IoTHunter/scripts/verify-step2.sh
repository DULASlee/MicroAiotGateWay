#!/bin/bash
set -euo pipefail
echo "=== Step 2 Auto Verification (Resilient & Cross-Platform) ==="

# 0. Clean and start
echo -n "Starting environment... "
docker compose -f docker-compose.local.yml down --remove-orphans > /dev/null 2>&1 || true
docker compose -f docker-compose.local.yml up -d --build > /dev/null 2>&1
echo "done"

# 1. Build
echo -n "Build... "
dotnet build IoTGateway/IoTGateway.csproj > /dev/null
echo "OK"

# 2. Polling wait for gateway health (max 30s)
echo -n "Waiting for Gateway healthy... "
for i in $(seq 1 30); do
  if curl -sf http://localhost:5080/health/ready > /dev/null 2>&1; then
    echo "OK (${i}s)"
    break
  fi
  if [ $i -eq 30 ]; then
    echo "FAIL (30s timeout)"
    docker compose -f docker-compose.local.yml logs iot-gateway
    exit 1
  fi
  sleep 1
done

# 3. Config query
echo -n "Config endpoint... "
curl -sf http://localhost:5080/api/v1/config | grep -q '"clientId":"iot-gateway-mqtt"' && echo "OK" || { echo "FAIL"; exit 1; }

# 4. HTTP normal telemetry
echo -n "HTTP telemetry... "
RES=$(curl -s -X POST http://localhost:5080/api/v1/telemetry \
  -H "Content-Type: application/json" \
  -d '{"deviceId":"v2","metricType":"t","timestamp":1717000000000,"sequence":1}')
echo "$RES" | grep -q '"status":"accepted"' && echo "OK" || { echo "FAIL: $RES"; exit 1; }

# 5. Kafka visibility
echo -n "Kafka message check... "
docker compose -f docker-compose.local.yml exec -T kafka \
  kafka-console-consumer --bootstrap-server kafka:9092 \
  --topic telemetry.raw --from-beginning --max-messages 1 --timeout-ms 10000 > /tmp/kafka_out.txt
grep -q '"deviceId":"v2"' /tmp/kafka_out.txt && echo "OK" || { echo "FAIL"; exit 1; }

# 6. Kafka down -> 503 degradation
echo -n "Kafka down -> 503... "
docker compose -f docker-compose.local.yml stop kafka > /dev/null
sleep 3
STATUS=$(curl -s -o /dev/null -w "%{http_code}" \
  -X POST http://localhost:5080/api/v1/telemetry \
  -H "Content-Type: application/json" \
  -d '{"deviceId":"x","metricType":"x","timestamp":1717000000000,"sequence":1}')
[ "$STATUS" = "503" ] && echo "OK" || { echo "FAIL: status $STATUS"; exit 1; }

# 7. Recover Kafka and verify 202
echo -n "Kafka recovery -> 202... "
docker compose -f docker-compose.local.yml start kafka > /dev/null
sleep 5
RES=$(curl -s -X POST http://localhost:5080/api/v1/telemetry \
  -H "Content-Type: application/json" \
  -d '{"deviceId":"recovery-test","metricType":"t","timestamp":1717000000000,"sequence":1}')
echo "$RES" | grep -q '"status":"accepted"' && echo "OK" || { echo "FAIL: $RES"; exit 1; }

echo "=== All Step 2 checks passed ==="
