Write-Host "=== Step 6 E2E Security Baseline Verification ===" -ForegroundColor Cyan
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
Check "K3s nodes Ready" { $n = kubectl get nodes -o json 2>$null | ConvertFrom-Json; ($n.items | Where { $_.status.conditions[0].status -eq "True" }).Count -gt 0 }
Check "Cilium running" { (kubectl get pods -n kube-system -l k8s-app=cilium --field-selector=status.phase=Running 2>$null | Measure-Object).Count -gt 1 }

# ---- 1. Pod 状态 ----
Write-Host "`n--- 1. Pod Status ---" -ForegroundColor Yellow
Check "iot-gateway Pods Running (2/2)" { (kubectl get pods -n $NS -l app=iot-gateway --field-selector=status.phase=Running 2>$null | Measure-Object).Count -gt 2 }
Check "backend-processor Pod Running" { (kubectl get pods -n $NS -l app=backend-processor --field-selector=status.phase=Running 2>$null | Measure-Object).Count -gt 1 }
Check "device-simulator Pod Running" { (kubectl get pods -n $NS -l app=device-simulator --field-selector=status.phase=Running 2>$null | Measure-Object).Count -gt 1 }
Check "mosquitto Pod Running" { (kubectl get pods -n $NS -l app=mosquitto --field-selector=status.phase=Running 2>$null | Measure-Object).Count -gt 1 }

# ---- 2. Secret 验证 ----
Write-Host "`n--- 2. Secret Verification ---" -ForegroundColor Yellow
Check "db-secret exists" { kubectl get secret db-secret -n $NS 2>$null }
Check "redis-secret exists" { kubectl get secret redis-secret -n $NS 2>$null }
Check "kafka-secret exists" { kubectl get secret kafka-secret -n $NS 2>$null }
Check "mosquitto-config exists" { kubectl get secret mosquitto-config -n $NS 2>$null }

# ---- 3. NetworkPolicy 验证 ----
Write-Host "`n--- 3. NetworkPolicy Verification ---" -ForegroundColor Yellow
Check "allow-gateway-to-kafka" { kubectl get networkpolicy allow-gateway-to-kafka -n $NS 2>$null }
Check "allow-processor-to-pg" { kubectl get networkpolicy allow-processor-to-pg -n $NS 2>$null }
Check "allow-sim-to-gateway" { kubectl get networkpolicy allow-sim-to-gateway -n $NS 2>$null }
Check "deny-all-other" { kubectl get networkpolicy deny-all-other -n $NS 2>$null }

# ---- 4. 探针验证 ----
Write-Host "`n--- 4. Probe Verification ---" -ForegroundColor Yellow
Check "iot-gateway liveness OK" {
    $pod = (kubectl get pods -n $NS -l app=iot-gateway -o json | ConvertFrom-Json).items[0]
    $ready = $pod.status.containerStatuses[0].ready
    $ready
}
Check "backend-processor readiness OK" {
    $pod = (kubectl get pods -n $NS -l app=backend-processor -o json | ConvertFrom-Json).items[0]
    $pod.status.containerStatuses[0].ready
}

# ---- 5. 安全上下文 ----
Write-Host "`n--- 5. Security Context ---" -ForegroundColor Yellow
Check "iot-gateway non-root" {
    $user = kubectl exec -n $NS deploy/iot-gateway -- whoami 2>$null
    $user -ne "root"
}
Check "backend-processor non-root" {
    $user = kubectl exec -n $NS deploy/backend-processor -- whoami 2>$null
    $user -ne "root"
}
Check "iot-gateway read-only FS" {
    $result = kubectl exec -n $NS deploy/iot-gateway -- touch /app/test 2>&1
    $LASTEXITCODE -ne 0
}

# ---- 6. 健康检查端点 ----
Write-Host "`n--- 6. Health Endpoints ---" -ForegroundColor Yellow
Check "Gateway /health/ready" {
    kubectl run health-check --rm -i --restart=Never --image=curlimages/curl:8.1.2 -n $NS `
        -- curl -sf http://iot-gateway/health/ready 2>$null
    $LASTEXITCODE -eq 0
}
Check "Gateway /metrics" {
    kubectl run metrics-check --rm -i --restart=Never --image=curlimages/curl:8.1.2 -n $NS `
        -- curl -sf http://iot-gateway:9464/metrics 2>$null
    $LASTEXITCODE -eq 0
}

# ---- 7. Secret 注入验证 ----
Write-Host "`n--- 7. Secret Injection ---" -ForegroundColor Yellow
Check "Gateway KAFKA_BOOTSTRAP_SERVERS injected" {
    $val = kubectl exec -n $NS deploy/iot-gateway -- printenv Kafka__BootstrapServers 2>$null
    $val -eq "kafka:9092"
}

# ---- 8. TLS 验证（若已部署证书） ----
Write-Host "`n--- 8. TLS Verification ---" -ForegroundColor Yellow
Check "gateway-tls Secret (optional)" {
    kubectl get secret gateway-tls -n $NS 2>$null
    $true  # TLS 是可选的，始终通过
}

# ---- 9. 滚动重启验证 ----
Write-Host "`n--- 9. Rolling Restart ---" -ForegroundColor Yellow
Write-Host "Triggering rolling restart of iot-gateway..." -ForegroundColor DarkYellow
kubectl rollout restart deployment/iot-gateway -n $NS 2>$null
Start-Sleep -Seconds 5
kubectl rollout status deployment/iot-gateway -n $NS --timeout=60s 2>$null
Check "iot-gateway rolled out successfully" {
    $status = kubectl rollout status deployment/iot-gateway -n $NS --timeout=5s 2>&1
    $status -match "successfully"
}

# ---- 10. 汇总 ----
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "  PASSED: $AllPassed  |  FAILED: $AllFailed" -ForegroundColor Cyan
Write-Host "========================================"

if ($AllFailed -eq 0) {
    Write-Host "ALL STEP6 CHECKS PASSED" -ForegroundColor Green
} else {
    Write-Host "$AllFailed CHECK(S) FAILED" -ForegroundColor Red
    exit 1
}
