param(
    [string]$Namespace = "iothunter"
)

$ErrorActionPreference = "Stop"
$CertDir = Join-Path $PSScriptRoot ".." "certs"

Write-Host "=== Generating TLS Certificates for IoTHunter ===" -ForegroundColor Cyan

# 确保证书目录存在
if (!(Test-Path $CertDir)) { New-Item -ItemType Directory -Path $CertDir | Out-Null }

Push-Location $CertDir

# 1. 生成 CA
Write-Host "Generating CA..." -ForegroundColor Yellow
openssl req -x509 -newkey rsa:4096 -days 365 -nodes `
    -keyout ca.key -out ca.crt `
    -subj "/CN=IoTHunter CA"

# 2. 生成 Gateway 证书（CN 对齐 K8s Service DNS）
Write-Host "Generating Gateway certificate..." -ForegroundColor Yellow
openssl req -newkey rsa:2048 -nodes -keyout gateway.key -out gateway.csr `
    -subj "/CN=iot-gateway.iothunter.svc"
openssl x509 -req -in gateway.csr -CA ca.crt -CAkey ca.key -CAcreateserial `
    -out gateway.crt -days 365

# 3. 生成 Mosquitto 证书
Write-Host "Generating Mosquitto certificate..." -ForegroundColor Yellow
openssl req -newkey rsa:2048 -nodes -keyout mosquitto.key -out mosquitto.csr `
    -subj "/CN=mosquitto.iothunter.svc"
openssl x509 -req -in mosquitto.csr -CA ca.crt -CAkey ca.key -CAcreateserial `
    -out mosquitto.crt -days 365

# 4. 清理 CSR 和 serial 文件
Remove-Item *.csr, *.srl -ErrorAction SilentlyContinue

Pop-Location

Write-Host "`nCertificates generated in: $CertDir" -ForegroundColor Green
Write-Host "Files: ca.crt, ca.key, gateway.crt, gateway.key, mosquitto.crt, mosquitto.key"

# 5. 提示 kubectl 命令
Write-Host "`nTo create K8s TLS Secrets, run:" -ForegroundColor Cyan
Write-Host "  kubectl create secret tls gateway-tls --cert=$CertDir/gateway.crt --key=$CertDir/gateway.key -n $Namespace"
Write-Host "  kubectl create secret tls mosquitto-tls --cert=$CertDir/mosquitto.crt --key=$CertDir/mosquitto.key -n $Namespace"
