# IoTHunter Step 1 NuGet 依赖安装脚本
# 在 IoTHunter 根目录执行: .\install-packages.ps1

$ErrorActionPreference = "Stop"

Write-Host "=== IoTHunter.Shared 公共依赖 ===" -ForegroundColor Cyan
dotnet add IoTHunter.Shared/IoTHunter.Shared.csproj package Serilog
dotnet add IoTHunter.Shared/IoTHunter.Shared.csproj package Serilog.Sinks.Console
dotnet add IoTHunter.Shared/IoTHunter.Shared.csproj package Microsoft.Extensions.Configuration.Abstractions
dotnet add IoTHunter.Shared/IoTHunter.Shared.csproj package Microsoft.Extensions.DependencyInjection.Abstractions
dotnet add IoTHunter.Shared/IoTHunter.Shared.csproj package OpenTelemetry
dotnet add IoTHunter.Shared/IoTHunter.Shared.csproj package OpenTelemetry.Extensions.Hosting
dotnet add IoTHunter.Shared/IoTHunter.Shared.csproj package OpenTelemetry.Exporter.OpenTelemetryProtocol
dotnet add IoTHunter.Shared/IoTHunter.Shared.csproj package OpenTelemetry.Instrumentation.Http
dotnet add IoTHunter.Shared/IoTHunter.Shared.csproj package OpenTelemetry.Instrumentation.Runtime

Write-Host "=== 三个项目公共依赖 ===" -ForegroundColor Cyan
$projects = @(
    "IoTGateway/IoTGateway.csproj",
    "BackendProcessor/BackendProcessor.csproj",
    "DeviceSimulator/DeviceSimulator.csproj"
)

foreach ($p in $projects) {
    Write-Host "--- $p ---" -ForegroundColor Yellow
    dotnet add $p package Serilog
    dotnet add $p package Serilog.Extensions.Hosting
    dotnet add $p package Serilog.Sinks.Console
    dotnet add $p package OpenTelemetry
    dotnet add $p package OpenTelemetry.Extensions.Hosting
    dotnet add $p package OpenTelemetry.Exporter.OpenTelemetryProtocol
    dotnet add $p package OpenTelemetry.Instrumentation.Http
    dotnet add $p package OpenTelemetry.Instrumentation.Runtime
}

Write-Host "=== Web 服务专属依赖 ===" -ForegroundColor Cyan
$webProjects = @(
    "IoTGateway/IoTGateway.csproj",
    "BackendProcessor/BackendProcessor.csproj"
)

foreach ($p in $webProjects) {
    Write-Host "--- $p ---" -ForegroundColor Yellow
    dotnet add $p package Serilog.AspNetCore
    dotnet add $p package OpenTelemetry.Instrumentation.AspNetCore
    dotnet add $p package OpenTelemetry.Exporter.Prometheus.AspNetCore
}

Write-Host "=== IoTGateway 专属依赖 ===" -ForegroundColor Cyan
dotnet add IoTGateway/IoTGateway.csproj package Confluent.Kafka
dotnet add IoTGateway/IoTGateway.csproj package Polly.Core
dotnet add IoTGateway/IoTGateway.csproj package MQTTnet
dotnet add IoTGateway/IoTGateway.csproj package MQTTnet.Extensions.ManagedClient

Write-Host "=== BackendProcessor 专属依赖 ===" -ForegroundColor Cyan
dotnet add BackendProcessor/BackendProcessor.csproj package Confluent.Kafka
dotnet add BackendProcessor/BackendProcessor.csproj package Npgsql
dotnet add BackendProcessor/BackendProcessor.csproj package StackExchange.Redis

Write-Host "=== DeviceSimulator 专属依赖 ===" -ForegroundColor Cyan
dotnet add DeviceSimulator/DeviceSimulator.csproj package Microsoft.Extensions.Hosting
dotnet add DeviceSimulator/DeviceSimulator.csproj package Microsoft.Extensions.Http
dotnet add DeviceSimulator/DeviceSimulator.csproj package MQTTnet

Write-Host ""
Write-Host "=== 安装完成! 执行 dotnet restore 验证 ===" -ForegroundColor Green
dotnet restore
Write-Host "=== 执行 dotnet build 验证 ===" -ForegroundColor Green
dotnet build
