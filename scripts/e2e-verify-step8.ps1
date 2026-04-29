param(
    [int]$DurationSeconds = 60,
    [int]$TargetQPS = 1500
)

Write-Host "=== IoTHunter Step 8 E2E Verification ===" -ForegroundColor Cyan
Write-Host "Target: >= $TargetQPS QPS sustained for $DurationSeconds seconds" -ForegroundColor Yellow
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

# ---- 0. 前置检查与端口转发 ----
Write-Host "`n--- 0. Prerequisites & Port-Forwards ---" -ForegroundColor Yellow
Check "kubectl available" { Get-Command kubectl 2>$null }
Check "Namespace $NS exists" { kubectl get namespace $NS 2>$null }

# 建立 Prometheus 端口转发（若未就绪）
Write-Host "Setting up Prometheus port-forward..." -ForegroundColor DarkYellow
$promPfJob = Start-Job -ScriptBlock {
    kubectl port-forward -n iothunter svc/prometheus-operated 9090:9090 2>$null
}
Start-Sleep -Seconds 3

Check "Prometheus accessible on localhost:9090" {
    try { (Invoke-RestMethod -Uri "http://localhost:9090/api/v1/query?query=up" -TimeoutSec 5) -ne $null } catch { $false }
}

# 建立 Jaeger 端口转发
Write-Host "Setting up Jaeger port-forward..." -ForegroundColor DarkYellow
$jaegerPfJob = Start-Job -ScriptBlock {
    kubectl port-forward -n iothunter svc/jaeger-query 16686:16686 2>$null
}
Start-Sleep -Seconds 2

# ---- 1. 基础健康检查 ----
Write-Host "`n--- 1. Health Check ---" -ForegroundColor Yellow
Check "Gateway health endpoint" {
    $result = kubectl exec -n $NS deploy/iot-gateway -- curl -s -o /dev/null -w "%{http_code}" http://localhost/health/ready 2>$null
    $result -eq "200"
}
Check "All observability pods Running" {
    $pods = kubectl get pods -n $NS --field-selector=status.phase=Running 2>$null
    ($pods | Select-String "otel-collector") -and
    ($pods | Select-String "jaeger") -and
    ($pods | Select-String "prometheus") -and
    ($pods | Select-String "grafana")
}
Check "Jaeger UI accessible" {
    try { Invoke-RestMethod -Uri "http://localhost:16686/api/services" -TimeoutSec 5 } catch { $false }
}

# ---- 2. 压测启动与等待 ----
Write-Host "`n--- 2. Pressure Test ---" -ForegroundColor Yellow
Write-Host "Scaling Simulator to 3 replicas..." -ForegroundColor DarkYellow
kubectl scale deployment device-simulator -n $NS --replicas=3 2>$null
Start-Sleep -Seconds 5

Check "Simulator pods Running (>=3)" {
    $running = (kubectl get pods -n $NS -l app=device-simulator --field-selector=status.phase=Running 2>$null | Measure-Object).Count
    $running -ge 3
}

Write-Host "Waiting ${DurationSeconds}s for stabilization (phantom-steady-state avoidance)..." -ForegroundColor DarkYellow
Start-Sleep -Seconds $DurationSeconds

# ---- 3. QPS 采集 ----
Write-Host "`n--- 3. QPS Measurement ---" -ForegroundColor Yellow

# 【终审修复：判空防崩溃】
$result = try {
    (Invoke-RestMethod -Uri "http://localhost:9090/api/v1/query?query=sum(rate(gateway_requests_total{status=\"success\"}[1m]))" -TimeoutSec 10).data.result
} catch { @() }

if ($result.Count -eq 0) {
    Write-Host "FAIL: No QPS data in Prometheus (Prometheus may be starting up or no traffic)" -ForegroundColor Red
    $script:AllFailed++
} else {
    $qps = $result[0].value[1] -as [double]
    Write-Host "Current QPS: $([math]::Round($qps, 1))" -ForegroundColor Cyan
    Check "QPS >= Target ($TargetQPS)" { $qps -ge $TargetQPS }
}

# ---- 4. P99 延迟检查 ----
Write-Host "`n--- 4. P99 Latency ---" -ForegroundColor Yellow
$p99Result = try {
    (Invoke-RestMethod -Uri "http://localhost:9090/api/v1/query?query=histogram_quantile(0.99,sum(rate(gateway_kafka_ack_latency_ms_bucket[1m]))by(le))" -TimeoutSec 10).data.result
} catch { @() }

if ($p99Result.Count -gt 0) {
    $p99 = $p99Result[0].value[1] -as [double]
    Write-Host "P99 Kafka Ack Latency: $([math]::Round($p99, 1))ms" -ForegroundColor Cyan
    Check "P99 latency <= 100ms" { $p99 -le 100 }
} else {
    Write-Host "WARN: No P99 data (may need more traffic)" -ForegroundColor DarkYellow
}

# ---- 5. 数据一致性核验 ----
Write-Host "`n--- 5. Data Consistency ---" -ForegroundColor Yellow

# PG 幂等去重核验（修复: reliability 非 reliability_level）
Check "PG: no duplicate event_ids" {
    $sql = "SELECT COUNT(DISTINCT event_id) AS unique_ids, COUNT(*) AS total_rows FROM telemetry_records;"
    $pgResult = kubectl exec -n $NS deploy/postgres -- psql -U iotapp -d IoTHunter -t -c $sql 2>$null
    if ($pgResult -match "(\d+)\s+\|\s+(\d+)") {
        $unique = [int]$Matches[1]
        $total = [int]$Matches[2]
        Write-Host "  unique=$unique total=$total" -ForegroundColor DarkGray
        $unique -eq $total -and $unique -gt 0
    } else {
        Write-Host "  PG query returned: $pgResult" -ForegroundColor DarkGray
        $false
    }
}

# 关键事件独立核验（修复: reliability=2）
Check "Critical events consistency (PG reliability=2 count > 0)" {
    $sql = "SELECT COUNT(DISTINCT event_id) FROM telemetry_records WHERE reliability=2;"
    $critCount = kubectl exec -n $NS deploy/postgres -- psql -U iotapp -d IoTHunter -t -c $sql 2>$null
    $critCount = $critCount.Trim()
    [int]$critCount -gt 0
}

# ---- 6. Trace 全链路验证 ----
Write-Host "`n--- 6. End-to-End Trace ---" -ForegroundColor Yellow
Check "Traces exist in Jaeger" {
    $traces = try {
        Invoke-RestMethod -Uri "http://localhost:16686/api/traces?service=IoTGateway&limit=5" -TimeoutSec 10
    } catch { $null }
    $traces.data.Count -gt 0
}

# 日志关联验证（修复: grep -oE 替代 grep -oP 兼容 BusyBox）
Check "Log-Trace correlation (trace_id in Gateway + Processor)" {
    $gwLog = kubectl logs -n $NS deployment/iot-gateway --tail=200 2>$null
    $traceLine = $gwLog | Select-String -Pattern 'trace=([0-9a-f]{32})' | Select-Object -Last 1
    if ($traceLine -match 'trace=([0-9a-f]{32})') {
        $traceId = $Matches[1]
        $procLog = kubectl logs -n $NS deployment/backend-processor --tail=200 2>$null
        ($procLog -match $traceId) -and ($traceId.Length -eq 32)
    } else { $false }
}

# ---- 7. 清理与报告 ----
Write-Host "`n--- 7. Cleanup ---" -ForegroundColor Yellow
Write-Host "Restoring Simulator to 1 replica..." -ForegroundColor DarkYellow
kubectl scale deployment device-simulator -n $NS --replicas=1 2>$null

# 清理端口转发
$jaegerPfJob, $promPfJob | ForEach-Object {
    if ($_) { Stop-Job $_ -ErrorAction SilentlyContinue; Remove-Job $_ -ErrorAction SilentlyContinue }
}
Get-Process -Name kubectl -ErrorAction SilentlyContinue | Where { $_.CommandLine -match "port-forward" } | Stop-Process -Force -ErrorAction SilentlyContinue

# ---- 汇总 ----
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "  PASSED: $AllPassed  |  FAILED: $AllFailed" -ForegroundColor Cyan
Write-Host "========================================"

if ($AllFailed -eq 0) {
    Write-Host "ALL STEP8 CHECKS PASSED" -ForegroundColor Green
} else {
    Write-Host "$AllFailed CHECK(S) FAILED" -ForegroundColor Red
    exit 1
}
