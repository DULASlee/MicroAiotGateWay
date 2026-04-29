Write-Host "=== Step 5 E2E Verification ===" -ForegroundColor Cyan
$ErrorActionPreference = "Stop"
$Network = "iot-e2e-net"

# 1. 创建网络
Write-Host "Creating docker network: $Network" -ForegroundColor Yellow
docker network create $Network -d bridge 2>$null

# 2. 启动基础设施
Write-Host "Starting infrastructure containers..." -ForegroundColor Yellow

docker run -d --name e2e-zookeeper --network $Network `
    -e ZOOKEEPER_CLIENT_PORT=2181 -e ZOOKEEPER_TICK_TIME=2000 `
    confluentinc/cp-zookeeper:7.6.0 2>&1

docker run -d --name e2e-kafka --network $Network `
    -e KAFKA_BROKER_ID=1 `
    -e KAFKA_ZOOKEEPER_CONNECT=e2e-zookeeper:2181 `
    -e KAFKA_ADVERTISED_LISTENERS=PLAINTEXT://e2e-kafka:9092 `
    -e KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1 `
    confluentinc/cp-kafka:7.6.0 2>&1

docker run -d --name e2e-postgres --network $Network `
    -e POSTGRES_DB=IoTHunter -e POSTGRES_USER=iotapp -e POSTGRES_PASSWORD=changeme `
    postgres:16 2>&1

docker run -d --name e2e-redis --network $Network `
    redis:7 2>&1

# 3. 构建并启动 Mosquitto
Write-Host "Building Mosquitto image..." -ForegroundColor Yellow
Push-Location (Join-Path $PSScriptRoot ".." "IoTHunter")
docker build -t mosquitto-iot:e2e -f infra/mosquitto/Dockerfile infra/mosquitto/ 2>&1
Pop-Location

docker run -d --name e2e-mosquitto --network $Network `
    -p 1883:1883 -p 8083:8083 `
    mosquitto-iot:e2e 2>&1

# 4. 等待基础设施就绪
Write-Host "Waiting for infrastructure (15s)..." -ForegroundColor DarkYellow
Start-Sleep -Seconds 15

# 5. 构建并启动 BackendProcessor
Write-Host "Building and starting BackendProcessor..." -ForegroundColor Yellow
Push-Location (Join-Path $PSScriptRoot ".." "IoTHunter")
docker build -t backend-processor:e2e -f BackendProcessor/Dockerfile . 2>&1
Pop-Location

docker run -d --name e2e-backend --network $Network `
    -p 5081:80 -p 9465:9464 `
    -e Kafka__BootstrapServers=e2e-kafka:9092 `
    -e Postgres__ConnectionString="Host=e2e-postgres;Database=IoTHunter;Username=iotapp;Password=changeme" `
    -e Redis__ConnectionString=e2e-redis:6379 `
    -e OpenTelemetry__OtlpEndpoint="" `
    backend-processor:e2e 2>&1

Start-Sleep -Seconds 5

# 6. 构建并启动 IoTGateway
Write-Host "Building and starting IoTGateway..." -ForegroundColor Yellow
Push-Location (Join-Path $PSScriptRoot ".." "IoTHunter")
docker build -t iot-gateway:e2e -f IoTGateway/Dockerfile . 2>&1
Pop-Location

docker run -d --name e2e-gateway --network $Network `
    -p 5080:80 -p 9464:9464 `
    -e Kafka__BootstrapServers=e2e-kafka:9092 `
    -e MqttBrokerWebSocketUrl="ws://e2e-mosquitto:8083/mqtt" `
    -e OpenTelemetry__OtlpEndpoint="" `
    iot-gateway:e2e 2>&1

Start-Sleep -Seconds 5

# 7. 健康检查
Write-Host "`n=== Health Checks ===" -ForegroundColor Yellow
try {
    $gw = Invoke-RestMethod -Uri "http://localhost:5080/health/ready" -TimeoutSec 5
    Write-Host "IoTGateway: $gw" -ForegroundColor Green
} catch {
    Write-Host "IoTGateway NOT healthy: $($_.Exception.Message)" -ForegroundColor Red
}

try {
    $bp = Invoke-RestMethod -Uri "http://localhost:5081/health/ready" -TimeoutSec 5
    Write-Host "BackendProcessor: $bp" -ForegroundColor Green
} catch {
    Write-Host "BackendProcessor NOT healthy: $($_.Exception.Message)" -ForegroundColor Red
}

# 8. 验证 Prometheus 指标
Write-Host "`n=== Metrics ===" -ForegroundColor Yellow
try {
    $metrics = Invoke-RestMethod -Uri "http://localhost:9464/metrics" -TimeoutSec 5
    Write-Host "IoTGateway /metrics accessible ($($metrics.Length) bytes)" -ForegroundColor Green
} catch {
    Write-Host "IoTGateway /metrics: $($_.Exception.Message)" -ForegroundColor DarkYellow
}

# 9. 验证 Mosquitto 匿名拒绝
Write-Host "`n=== Mosquitto Auth ===" -ForegroundColor Yellow
$mosqAnon = docker run --rm --network $Network eclipse-mosquitto:2.0.18 `
    mosquitto_pub -h e2e-mosquitto -t "device/test/telemetry" -m "test" 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "Anonymous publish rejected (correct)" -ForegroundColor Green
} else {
    Write-Host "WARNING: Anonymous publish allowed" -ForegroundColor Red
}

# 10. 清理
Write-Host "`n=== Cleanup ===" -ForegroundColor Yellow
$containers = @("e2e-gateway", "e2e-backend", "e2e-mosquitto", "e2e-kafka", "e2e-zookeeper", "e2e-postgres", "e2e-redis")
foreach ($c in $containers) {
    docker rm -f $c 2>$null
}
docker network rm $Network 2>$null

Write-Host "`n=== Step 5 E2E Verification Complete ===" -ForegroundColor Cyan
Write-Host "NOTE: Full data consistency check (PG count vs Simulator sent) requires a running Simulator container."
Write-Host "Run Step 4 E2E script against containerized services for the full data-path test."
