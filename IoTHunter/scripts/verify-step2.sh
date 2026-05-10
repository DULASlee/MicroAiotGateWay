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

docker rm -f gw-step2 > /dev/null 2>&1 || true
echo "=== All Step 2 checks passed ==="
