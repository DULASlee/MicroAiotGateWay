param(
    [string]$Tag = "latest",
    [string]$Registry = ""
)

$ErrorActionPreference = "Stop"
$prefix = if ($Registry) { "$Registry/" } else { "" }

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location (Join-Path $repoRoot "IoTHunter")

Write-Host "Building IoTGateway..." -ForegroundColor Cyan
docker build -t "${prefix}iot-gateway:${Tag}" -f IoTGateway/Dockerfile .

Write-Host "Building BackendProcessor..." -ForegroundColor Cyan
docker build -t "${prefix}backend-processor:${Tag}" -f BackendProcessor/Dockerfile .

Write-Host "Building DeviceSimulator..." -ForegroundColor Cyan
docker build -t "${prefix}device-simulator:${Tag}" -f DeviceSimulator/Dockerfile .

Write-Host "Building Mosquitto..." -ForegroundColor Cyan
docker build -t "${prefix}mosquitto-iot:${Tag}" -f infra/mosquitto/Dockerfile infra/mosquitto/

Write-Host "All images built with tag: ${Tag}" -ForegroundColor Green
docker images --filter "reference=*${Tag}" --format "table {{.Repository}}:{{.Tag}}\t{{.Size}}"
