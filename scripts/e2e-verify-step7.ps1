Write-Host "=== Step 7 E2E Observability Verification ===" -ForegroundColor Cyan
$ErrorActionPreference = "Continue"
$NS = "iothunter"
$AllPassed = 0
$AllFailed = 0

function Check {
    param($Label, [ScriptBlock]$Test)
    Write-Host "[CHECK] $Label ... " -NoNewline
    try {
        if (& $Test) {
            Write-Host "PASSED" -ForegroundColor Green
            $script:AllPassed++
        } else {
            Write-Host "FAILED" -ForegroundColor Red
            $script:AllFailed++
        }
    } catch {
        Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
        $script:AllFailed++
    }
}

# ---- 0. 前置检查 ----
Write-Host "`n--- 0. Prerequisites ---" -ForegroundColor Yellow
Check "kubectl available" { Get-Command kubectl 2>$null }
Check "Namespace $NS exists" { kubectl get namespace $NS 2>$null }
Check "K3s nodes Ready" {
    $n = kubectl get nodes -o json 2>$null | ConvertFrom-Json
    ($n.items | Where { $_.status.conditions[0].status -eq "True" }).Count -gt 0
}

# ---- 1. OTel Collector DaemonSet ----
Write-Host "`n--- 1. OTel Collector ---" -ForegroundColor Yellow
Check "otel-collector DaemonSet exists" {
    kubectl get daemonset -n $NS otel-collector 2>$null
}
Check "otel-collector pods Running" {
    $ready = kubectl get daemonset -n $NS otel-collector -o json 2>$null | ConvertFrom-Json
    $ready.status.desiredNumberScheduled -eq $ready.status.numberReady
}
Check "otel-collector log confirms ready" {
    $log = kubectl logs -n $NS -l app.kubernetes.io/name=opentelemetry-collector --tail=20 2>$null
    $log -match "Everything is ready"
}
Check "OTLP connectivity (gateway -> collector)" {
    $result = kubectl exec -n $NS deploy/iot-gateway -- curl -s http://localhost:4317/v1/traces 2>$null
    $result -match "partial success"
}

# ---- 2. Jaeger ----
Write-Host "`n--- 2. Jaeger ---" -ForegroundColor Yellow
Check "jaeger Pod Running" {
    (kubectl get pods -n $NS -l app=jaeger --field-selector=status.phase=Running 2>$null | Measure-Object).Count -gt 1
}
Check "jaeger-query Service exists" {
    kubectl get svc -n $NS jaeger-query 2>$null
}
Check "jaeger-collector Service exists" {
    kubectl get svc -n $NS jaeger-collector 2>$null
}

# Port-forward Jaeger and query API
$jaegerPfJob = Start-Job -ScriptBlock {
    kubectl port-forward -n iothunter svc/jaeger-query 16686:16686 2>$null
}
Start-Sleep -Seconds 3
Check "Jaeger API returns services" {
    $svc = try { Invoke-RestMethod -Uri "http://localhost:16686/api/services" -TimeoutSec 10 } catch { $null }
    $svc -ne $null -and $svc.data -contains "IoTGateway"
}

# ---- 3. Prometheus + Grafana ----
Write-Host "`n--- 3. kube-prometheus-stack ---" -ForegroundColor Yellow
Check "prometheus Pod Running" {
    (kubectl get pods -n $NS -l app.kubernetes.io/name=prometheus --field-selector=status.phase=Running 2>$null | Measure-Object).Count -gt 1
}
Check "grafana Pod Running" {
    (kubectl get pods -n $NS -l app.kubernetes.io/name=grafana --field-selector=status.phase=Running 2>$null | Measure-Object).Count -gt 1
}

# Port-forward Prometheus
$promPfJob = Start-Job -ScriptBlock {
    kubectl port-forward -n iothunter svc/prometheus-operated 9090:9090 2>$null
}
Start-Sleep -Seconds 3
Check "Prometheus targets: iot-gateway UP" {
    $targets = try { Invoke-RestMethod -Uri "http://localhost:9090/api/v1/targets" -TimeoutSec 10 } catch { $null }
    $target = $targets.data.activeTargets | Where { $_.labels.app -eq "iot-gateway" }
    $target.health -eq "up"
}
Check "Prometheus targets: backend-processor UP" {
    $targets = try { Invoke-RestMethod -Uri "http://localhost:9090/api/v1/targets" -TimeoutSec 10 } catch { $null }
    $target = $targets.data.activeTargets | Where { $_.labels.app -eq "backend-processor" }
    $target.health -eq "up"
}
Check "Prometheus targets: device-simulator UP" {
    $targets = try { Invoke-RestMethod -Uri "http://localhost:9090/api/v1/targets" -TimeoutSec 10 } catch { $null }
    $target = $targets.data.activeTargets | Where { $_.labels.app -eq "device-simulator" }
    $target.health -eq "up"
}
Check "Prometheus metric gateway_requests_total exists" {
    $query = try { Invoke-RestMethod -Uri "http://localhost:9090/api/v1/query?query=gateway_requests_total" -TimeoutSec 10 } catch { $null }
    $query.data.result.Count -gt 0
}

# ---- 4. 全链路 Trace 验证 ----
Write-Host "`n--- 4. End-to-End Trace ---" -ForegroundColor Yellow
Check "Send test message to gateway" {
    $body = @{
        deviceId = "dev-e2e-test"
        metricType = "heart_rate"
        payload = @{ bpm = 72 }
        timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        sequence = 1
    } | ConvertTo-Json
    $result = kubectl exec -n $NS deploy/device-simulator -- curl -s -o /dev/null -w "%{http_code}" `
        -X POST http://iot-gateway/api/v1/telemetry `
        -H "Content-Type: application/json" `
        -d $body 2>$null
    $result -eq "202"
}
Write-Host "Waiting 10s for trace propagation..." -ForegroundColor DarkYellow
Start-Sleep -Seconds 10
Check "Trace appears in Jaeger" {
    $traces = try {
        Invoke-RestMethod -Uri "http://localhost:16686/api/traces?service=IoTGateway&limit=5" -TimeoutSec 10
    } catch { $null }
    $traces.data.Count -gt 0
}

# ---- 5. 日志关联验证 ----
Write-Host "`n--- 5. Log-Trace Correlation ---" -ForegroundColor Yellow
Check "Gateway log contains trace_id" {
    $log = kubectl logs -n $NS deployment/iot-gateway --tail=100 2>$null
    $traceId = [regex]::Match($log, 'trace[\s=]*([0-9a-f]{32})').Groups[1].Value
    $traceId.Length -eq 32
}

# ---- 6. Grafana ----
Write-Host "`n--- 6. Grafana ---" -ForegroundColor Yellow
$grafPfJob = Start-Job -ScriptBlock {
    kubectl port-forward -n iothunter svc/prometheus-grafana 3000:80 2>$null
}
Start-Sleep -Seconds 3
Check "Grafana API accessible" {
    $response = try {
        Invoke-RestMethod -Uri "http://admin:IoTHunter2026!@localhost:3000/api/dashboards/home" -TimeoutSec 10
    } catch { $null }
    $response -ne $null
}

# ---- 7. 清理 port-forward ----
Write-Host "`n--- Cleaning up port-forwards ---" -ForegroundColor Yellow
$jaegerPfJob, $promPfJob, $grafPfJob | ForEach-Object {
    if ($_) { Stop-Job $_ -ErrorAction SilentlyContinue; Remove-Job $_ -ErrorAction SilentlyContinue }
}
# Kill any lingering kubectl port-forward processes
Get-Process -Name kubectl -ErrorAction SilentlyContinue | Where { $_.CommandLine -match "port-forward" } | Stop-Process -Force -ErrorAction SilentlyContinue

# ---- 8. 汇总 ----
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "  PASSED: $AllPassed  |  FAILED: $AllFailed" -ForegroundColor Cyan
Write-Host "========================================"

if ($AllFailed -eq 0) {
    Write-Host "ALL STEP7 CHECKS PASSED" -ForegroundColor Green
} else {
    Write-Host "$AllFailed CHECK(S) FAILED" -ForegroundColor Red
    exit 1
}
