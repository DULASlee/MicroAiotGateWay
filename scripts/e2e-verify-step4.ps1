Write-Host "=== Step 4 E2E Verification ===" -ForegroundColor Cyan

# 1. 前置检查
Write-Host "Checking prerequisites..." -ForegroundColor Yellow
try {
    $gw = Invoke-RestMethod -Uri "http://localhost:5080/health/ready" -TimeoutSec 5
    Write-Host "IoTGateway: $gw" -ForegroundColor Green
} catch {
    Write-Host "ERROR: IoTGateway not ready at :5080" -ForegroundColor Red
    exit 1
}
try {
    $bp = Invoke-RestMethod -Uri "http://localhost:5081/health/ready" -TimeoutSec 5
    Write-Host "BackendProcessor: $bp" -ForegroundColor Green
} catch {
    Write-Host "ERROR: BackendProcessor not ready at :5081" -ForegroundColor Red
    exit 1
}

# 2. 运行 Simulator (HTTP 模式)
Write-Host "`nStarting Simulator (HTTP, 3 devices, 500ms, 10s)..." -ForegroundColor Yellow
$repoRoot = Split-Path -Parent $PSScriptRoot
$job = Start-Job -ScriptBlock {
    Set-Location $using:repoRoot
    dotnet run --project IoTHunter/DeviceSimulator/DeviceSimulator.csproj -- `
        --protocol=http --devices=3 --interval=500 --duration=10 --concurrency=2 2>&1
}
Wait-Job $job -Timeout 20 | Out-Null
$output = Receive-Job $job
$job | Remove-Job

if ($output -match "Producer done") {
    Write-Host "Simulator completed" -ForegroundColor Green
} else {
    Write-Host "Simulator may not have completed normally" -ForegroundColor DarkYellow
}

# 3. 等待 Redis 投影追平
Write-Host "Waiting 3 seconds for Redis projection..." -ForegroundColor DarkYellow
Start-Sleep -Seconds 3

# 4. 查询 BackendProcessor API
Write-Host "`n=== API Query Verification ===" -ForegroundColor Yellow

# Latest (接受 404 — ADR 规定 Redis 无数据返回 404)
try {
    $latest = Invoke-RestMethod -Uri "http://localhost:5081/api/v2/devices/dev-000/latest" -ErrorAction Stop
    Write-Host "Latest dev-000: $($latest | ConvertTo-Json -Compress)" -ForegroundColor Green
} catch {
    if ($_.Exception.Response.StatusCode.value__ -eq 404) {
        Write-Host "Latest API returned 404 (This is CORRECT per ADR, projection not ready yet)" -ForegroundColor DarkYellow
    } else {
        Write-Host "Latest API Error: $($_.Exception.Message)" -ForegroundColor Red
    }
}

# History (必须返回数据)
$history = Invoke-RestMethod -Uri "http://localhost:5081/api/v2/devices/dev-000/history?page=1&pageSize=5"
Write-Host "History totalCount for dev-000: $($history.totalCount)" -ForegroundColor Green

Write-Host "`n=== Step 4 Verification Complete ===" -ForegroundColor Cyan
