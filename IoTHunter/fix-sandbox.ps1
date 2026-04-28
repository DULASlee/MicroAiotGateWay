# fix-sandbox.ps1 — 修复 trae-sandbox 找不到 dotnet 的问题
# 请在 IDE 外面的 PowerShell 中运行（不需要管理员权限也可尝试）
# 如果不行，右键 → 以管理员身份运行 PowerShell，再执行此脚本

$ErrorActionPreference = "Continue"
Write-Host "=== IoTHunter Sandbox 修复脚本 ===" -ForegroundColor Cyan
Write-Host ""

# 1. 定位 dotnet.exe 真实路径
Write-Host "[1/4] 查找 dotnet.exe..." -ForegroundColor Yellow

$dotnetPath = $null
$candidates = @(
    "C:\Program Files\dotnet\dotnet.exe",
    "$env:ProgramFiles\dotnet\dotnet.exe",
    "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe",
    "$env:USERPROFILE\.dotnet\dotnet.exe"
)

foreach ($c in $candidates) {
    if (Test-Path $c) {
        $dotnetPath = $c
        Write-Host "  ✓ 找到: $c" -ForegroundColor Green
        $ver = & $c --version 2>&1
        Write-Host "    版本: $ver" -ForegroundColor Green
        break
    }
}

if (-not $dotnetPath) {
    Write-Host "  ✗ 未找到 dotnet.exe，请先安装 .NET SDK" -ForegroundColor Red
    pause
    exit 1
}

# 2. 检查 PATH
Write-Host ""
Write-Host "[2/4] 检查环境变量 PATH..." -ForegroundColor Yellow

$dotnetDir = Split-Path $dotnetPath -Parent
$machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")

if ($machinePath -notlike "*$dotnetDir*" -and $userPath -notlike "*$dotnetDir*") {
    Write-Host "  ! dotnet 目录不在 PATH 中，正在添加到用户 PATH..." -ForegroundColor Magenta
    $newUserPath = "$dotnetDir;$userPath"
    [Environment]::SetEnvironmentVariable("Path", $newUserPath, "User")
    $env:Path = "$dotnetDir;$env:Path"
    Write-Host "  ✓ 已添加到用户 PATH" -ForegroundColor Green
} else {
    Write-Host "  ✓ dotnet 已在 PATH 中" -ForegroundColor Green
}

# 3. 检查 trae-sandbox 是否可执行
Write-Host ""
Write-Host "[3/4] 验证 trae-sandbox..." -ForegroundColor Yellow

$sandboxPath = "$env:LOCALAPPDATA\trae-cli\bin\trae-sandbox.exe"
if (Test-Path $sandboxPath) {
    Write-Host "  ✓ trae-sandbox.exe 存在: $sandboxPath" -ForegroundColor Green
    try {
        $result = & $sandboxPath 'dotnet --version' 2>&1
        Write-Host "  测试输出: $result" -ForegroundColor Green
    } catch {
        Write-Host "  ✗ trae-sandbox.exe 执行失败: $_" -ForegroundColor Red
        
        Write-Host ""
        Write-Host "[4/4] 尝试重装 trae-cli..." -ForegroundColor Yellow
        Write-Host "  如果以上步骤不行，请执行重装命令:"
        Write-Host "  irm https://trae.cn/trae-cli/install.ps1 | iex" -ForegroundColor White
    }
} else {
    Write-Host "  ! trae-sandbox.exe 不存在于 $sandboxPath" -ForegroundColor Magenta
    Write-Host "  → 搜索 trae-sandbox..."
    $found = Get-ChildItem -Path "$env:LOCALAPPDATA" -Filter "trae-sandbox.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) {
        Write-Host "  ✓ 找到: $($found.FullName)" -ForegroundColor Green
    } else {
        Write-Host "  ✗ 未找到，可能需要安装 trae-cli" -ForegroundColor Red
    }
}

# 4. 验证 dotnet 在沙箱外正常工作
Write-Host ""
Write-Host "[最终验证] dotnet SDK 列表:" -ForegroundColor Cyan
& $dotnetPath --list-sdks

Write-Host ""
Write-Host "=== 修复完成 ===" -ForegroundColor Cyan
Write-Host "请重启 Trae IDE 使 PATH 变更生效，然后回终端再试。" -ForegroundColor White
pause
