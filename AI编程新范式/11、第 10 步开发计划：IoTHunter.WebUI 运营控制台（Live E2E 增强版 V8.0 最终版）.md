# 第 10 步开发计划：IoTHunter.WebUI 运营控制台（Live E2E 增强版 V8.0 最终版）

> **版本**：V8.0（熔接三次裁决：JWT Token 传递、Blazor 线程安全修复、环境变量注入 BaseUrl、Bootstrap CSS 引入、错误处理增强、_Host.cshtml 验证、页面代码补全）  
> **对应总架构**：`架构设计文档 V7.0` 第 10 步  
> **前置依赖**：第 9 步 IoTHunter.Management（JWT 鉴权已启用）、第 3 步 BackendProcessor（TraceController + SignalR）、第 4 步 DeviceSimulator（snapshot API）、第 7 步可观测性栈均已部署且可用  
> **本步目标**：开发独立的 **Blazor Server** 运营控制台，将设备配置、全链路拓扑、实时监控和压测控制四大功能可视化。UI 只通过预留 API 获取数据，不侵入核心逻辑。最终让非技术决策者（老板/客户）可直接在浏览器上操作和演示整个数据网关系统。  
> **本步边界**：只实现前端展示与交互，不修改任何后端服务代码。  
> **核心禁令**：  
> - **硬编码禁令（ADR-022）**：所有后端 URL 通过环境变量注入，`appsettings.json` 仅提供本地默认值，禁止硬编码。  
> - **Mock 数据禁令（ADR-023）**：验证时必须调用真实的后端接口获取数据。  
> - **UI 边界（ADR-024）**：UI 只能通过已预留的 API 获取数据，禁止直连数据库、Kafka 或 Mosquitto。  
> **【V8.0 关键修正】**：  
> - **JWT（P10-02：`AuthTokenHolder` Scoped + `ManagementApiClient` 逐请求 Bearer）**：**禁止** `DefaultRequestHeaders.Authorization`；受保护路由一律 `HttpRequestMessage` + `SendAsync`。  
> - **Simulator DTO（P10-03 / P10-01）**：`SimulatorConfig` 使用 `IntervalMs`、`Concurrency`、`DurationSeconds`、`CriticalEventRatio`；`SimulatorSnapshot` 使用 `TotalSent`、`TotalFailed`、`LatencyP99Ms`、`LatencyMaxMs`（**无** `TotalSuccess` / `LatencyP99` / `P50`）。  
> - **Blazor 线程安全修复**：`Dashboard.razor` 后台轮询使用 `InvokeAsync(() => { ... StateHasChanged(); })` 更新 UI 状态，杜绝白屏崩溃。  
> - **环境变量注入 BaseUrl**：所有 API 端点地址通过 `appsettings.json` 提供默认值，K8s 部署时通过环境变量覆盖。  
> - **Bootstrap CSS 引入**：`_Host.cshtml` 中引入 Bootstrap 5 CDN，确保美观的深色主题商业界面。  
> - **错误处理增强**：所有数据获取页面增加错误状态显示和重试提示。  
> - **页面代码补全**：`TraceViewer.razor` 和 `LoadTest.razor` 补全基础代码框架（压测页与 snapshot/config 字段对齐）。  
> - **SignalR 客户端（P10-04）**：`MonitoringService.cs` 为 **占位**；本步 `Dashboard` **不**注入、**不**在 `Program.cs` 注册，待对接 `MonitoringHub` 再启用。  
> - **Dockerfile 安装 curl**：确保容器健康检查可用。  
> - **K8s NetworkPolicy（P-F1）**：§1.8.1 给出 WebUI 三条专用策略 `allow-webui-egress`、`allow-webui-to-management`、`allow-webui-to-backends`（与第 6 步零信任配套）。

---

## 0. 前置检查

| 检查项                            | 位置 / 验证命令                                      | 状态要求                    |
| --------------------------------- | ---------------------------------------------------- | --------------------------- |
| IoTGateway 运行                   | `curl -s http://localhost:5080/health/ready`         | 返回 `Healthy`              |
| BackendProcessor 运行             | `curl -s http://localhost:5081/health/ready`         | 返回 `ready`                |
| DeviceSimulator 运行              | `curl -s http://localhost:5091/api/simulator/health` | 返回 `{"status":"running"}` |
| IoTHunter.Management 运行         | `curl -s http://localhost:5082/health/ready`         | 返回 `ready`                |
| Jaeger 可访问                     | 浏览器打开 `http://localhost:16686`                  | Jaeger UI 正常              |
| 第 9 步验证通过                   | `screenshots/step9/` 下存在全部验证证据              | 10 项剧本已勾选             |
| .NET 10 SDK                       | `dotnet --version`                                   | 10.x.xxx                    |
| `IoTHunter.Infrastructure` 可编译 | `dotnet build IoTHunter.Infrastructure`              | 0 Error, 0 Warning          |

---

## 1. 动作项

### 1.1 创建 IoTHunter.WebUI 项目

**操作**：在解决方案根目录执行以下命令：

```bash
cd IoTHunter
dotnet new blazorserver -n IoTHunter.WebUI -f net10.0 --no-https
dotnet sln add IoTHunter.WebUI/IoTHunter.WebUI.csproj
dotnet add IoTHunter.WebUI/IoTHunter.WebUI.csproj reference IoTHunter.Shared/IoTHunter.Shared.csproj
dotnet add IoTHunter.WebUI/IoTHunter.WebUI.csproj reference IoTHunter.Infrastructure/IoTHunter.Infrastructure.csproj
```

**验证**：`dotnet build IoTHunter.WebUI/IoTHunter.WebUI.csproj` 0 Error, 0 Warning。☐

---

### 1.2 安装 NuGet 依赖

```bash
dotnet add IoTHunter.WebUI/IoTHunter.WebUI.csproj package Microsoft.AspNetCore.SignalR.Client
dotnet add IoTHunter.WebUI/IoTHunter.WebUI.csproj package System.Net.Http.Json
```

**验证**：`dotnet restore IoTHunter.WebUI/IoTHunter.WebUI.csproj` 成功。☐

---

### 1.3 更新配置文件（V8.0：环境变量注入 + localhost 默认值）

**操作**：完全替换 `IoTHunter.WebUI/appsettings.json` 为以下内容：

```json
{
  "AllowedHosts": "*",
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://localhost:5083"
      }
    }
  },
  "ApiEndpoints": {
    "ManagementBaseUrl": "http://localhost:5082",
    "BackendProcessorBaseUrl": "http://localhost:5081",
    "DeviceSimulatorBaseUrl": "http://localhost:5091",
    "JaegerQueryUrl": "http://localhost:16686"
  }
}
```

**【V8.0 关键说明】**：
- 以上均为本地开发的默认值
- K8s 部署时通过环境变量覆盖：
  - `ApiEndpoints__ManagementBaseUrl=http://iothunter-management:80`
  - `ApiEndpoints__BackendProcessorBaseUrl=http://backend-processor:80`
  - `ApiEndpoints__DeviceSimulatorBaseUrl=http://device-simulator:5091`
  - `ApiEndpoints__JaegerQueryUrl=http://jaeger-query:16686`

**验证**：`cat IoTHunter.WebUI/appsettings.json` 确认内容正确。☐

---

### 1.4 创建 API 服务客户端（V8.0：JWT Token 传递）

**操作**：依次新建以下服务类，每新建一个后执行 `dotnet build IoTHunter.WebUI/IoTHunter.WebUI.csproj`。

#### 1.4.1 `AuthTokenHolder` + `ManagementApiClient`（V8.0 终审：Scoped JWT + 逐请求 Bearer，避免 Typed `HttpClient` Transient 丢 Token）

**背景**：`AddHttpClient<ManagementApiClient>()` 注册的客户端常为 **Transient**，若在 `LoginAsync` 里写 `HttpClient.DefaultRequestHeaders.Authorization`，后续页面用到的可能是**另一个** `ManagementApiClient` 实例，导致 **Token 未携带、API 全部 401**。解法：用 **Scoped** 的 `AuthTokenHolder` 保存令牌，每个 API 调用用 **`HttpRequestMessage` + `SendAsync`** 单独附加 `Authorization` 头（不污染共享 `HttpClient` 默认头）。

**（P10-02）** 必须与上述一致：**Scoped** `AuthTokenHolder` + **`ManagementApiClient` 构造函数注入**该 Holder；Protected 路由 **只能**通过 `AttachBearer(request)`/`SendAsync`**逐请求挂头**。**严禁** `_http.DefaultRequestHeaders.Authorization`。

**操作一**：新建 `IoTHunter.WebUI/Services/AuthTokenHolder.cs`，粘贴以下内容：

```csharp
namespace IoTHunter.WebUI.Services;

/// <summary>与 Blazor Server 线路同生命周期的 JWT，供 ManagementApiClient 各次请求读取。</summary>
public sealed class AuthTokenHolder
{
    public string? BearerToken { get; set; }
}
```

**操作二**：新建 `IoTHunter.WebUI/Services/ManagementApiClient.cs`，粘贴以下内容：

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace IoTHunter.WebUI.Services;

public class ManagementApiClient
{
    private readonly HttpClient _http;
    private readonly AuthTokenHolder _auth;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ManagementApiClient(HttpClient http, AuthTokenHolder auth)
    {
        _http = http;
        _auth = auth;
    }

    public async Task<bool> LoginAsync(string username, string password)
    {
        _auth.BearerToken = null;
        var response = await _http.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        if (!response.IsSuccessStatusCode)
            return false;
        var result = await response.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions);
        if (result?.Token is null)
            return false;
        _auth.BearerToken = result.Token;
        return true;
    }

    private void AttachBearer(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_auth.BearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _auth.BearerToken);
    }

    public async Task<DeviceCredentials?> GetDeviceCredentialsAsync(string deviceId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/devices/{deviceId}/credentials");
        AttachBearer(request);
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DeviceCredentials>(JsonOptions);
    }

    public async Task<GatewayStatus?> GetGatewayStatusAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/gateway/status");
        AttachBearer(request);
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GatewayStatus>(JsonOptions);
    }
}

public record LoginResponse(string Token, string Username);
public record DeviceCredentials(string DeviceId, string MqttBroker, string Username, string Password, string TopicTelemetry, string TopicCritical);
public record GatewayStatus(object Gateway, object DefaultDeviceCredentials);
```

**验证**：`dotnet build IoTHunter.WebUI/IoTHunter.WebUI.csproj` 0 Error, 0 Warning。☐

#### 1.4.2 BackendProcessorApiClient

新建 `IoTHunter.WebUI/Services/BackendProcessorApiClient.cs`，粘贴以下内容：

```csharp
using System.Net.Http.Json;
using System.Text.Json;

namespace IoTHunter.WebUI.Services;

public class BackendProcessorApiClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public BackendProcessorApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<List<TraceSummary>?> GetTracesAsync(string deviceId, int limit = 10)
    {
        return await _http.GetFromJsonAsync<List<TraceSummary>>($"/api/v2/traces/{deviceId}?limit={limit}", JsonOptions);
    }

    public async Task<DeviceLatest?> GetDeviceLatestAsync(string deviceId)
    {
        return await _http.GetFromJsonAsync<DeviceLatest>($"/api/v2/devices/{deviceId}/latest", JsonOptions);
    }
}

public record TraceSummary(string TraceId, List<TraceSegment> Segments);
public record TraceSegment(string OperationName, double DurationMs);
public record DeviceLatest(Dictionary<string, string> Data);
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

#### 1.4.3 SimulatorApiClient（**P10-03：`SimulatorSnapshot` / `SimulatorConfig` 对齐 DeviceSimulator `/api/simulator/snapshot`、`/config`**）

新建 `IoTHunter.WebUI/Services/SimulatorApiClient.cs`，粘贴以下内容（**record 字段与 `DeviceSimulator` 的 `/api/simulator/snapshot`、`/api/simulator/config` JSON 一致**；与仓库 `SimulatorMetrics.GetSnapshot()`、`Program.cs` 匿名 config 对象对齐）。

```csharp
using System.Net.Http.Json;
using System.Text.Json;

namespace IoTHunter.WebUI.Services;

public class SimulatorApiClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SimulatorApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<SimulatorSnapshot?> GetSnapshotAsync()
    {
        return await _http.GetFromJsonAsync<SimulatorSnapshot>("/api/simulator/snapshot", JsonOptions);
    }

    public async Task<SimulatorConfig?> GetConfigAsync()
    {
        return await _http.GetFromJsonAsync<SimulatorConfig>("/api/simulator/config", JsonOptions);
    }
}

public record SimulatorSnapshot(
    long TotalSent,
    long TotalFailed,
    double SuccessRate,
    double Qps,
    double LatencyP99Ms,
    double LatencyMaxMs);

public record SimulatorConfig(
    string Protocol,
    string Target,
    int DeviceCount,
    int IntervalMs,
    int Concurrency,
    int DurationSeconds,
    double CriticalEventRatio);
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.5 实现 SignalR 实时监控客户端（**P10-04：占位，当前 Dashboard 不接入**）

**【P10-04】本步不要求在 `Dashboard.razor` 调用 `MonitoringService.StartAsync`**：监控大屏以 §1.4.3 **`SimulatorApi` 轮询** `/api/simulator/snapshot` 为准。**§1.8 `Program.cs` 暂不注册** `MonitoringService`，避免无用的单例。保留 `MonitoringService.cs` **供后续**：与 BackendProcessor `MonitoringHub`（`/hubs/monitoring`）联通后，再 `await MonitoringService.StartAsync(hubAbsoluteUrl)`、`AddSingleton` 注册并从页面订阅 `OnMetricsReceived`。

**操作**：新建 `IoTHunter.WebUI/Services/MonitoringService.cs`，粘贴以下内容：

```csharp
using Microsoft.AspNetCore.SignalR.Client;

namespace IoTHunter.WebUI.Services;

public class MonitoringService : IAsyncDisposable
{
    private HubConnection? _hubConnection;
    public event Action<MonitoringMetrics>? OnMetricsReceived;
    public event Action<string>? OnError;

    public async Task StartAsync(string hubUrl)
    {
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<MonitoringMetrics>("MetricsUpdate", metrics =>
        {
            OnMetricsReceived?.Invoke(metrics);
        });

        _hubConnection.Closed += async (error) =>
        {
            OnError?.Invoke($"SignalR disconnected: {error?.Message}");
            await Task.Delay(5000);
            try { await _hubConnection.StartAsync(); } catch { }
        };

        await _hubConnection.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection is not null)
        {
            await _hubConnection.DisposeAsync();
        }
    }
}

public record MonitoringMetrics(double Qps, long KafkaLag, double PgTps, double P99);
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.6 验证 _Host.cshtml 并引入 Bootstrap CSS（V8.0）

**操作**：确认 `IoTHunter.WebUI/Pages/_Host.cshtml` 存在，并在 `<head>` 标签中添加 Bootstrap 5 CDN：

```html
<!DOCTYPE html>
<html lang="zh-CN">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <base href="~/" />
    <title>IoTHunter 运营控制台</title>
    <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/css/bootstrap.min.css" rel="stylesheet">
    <link href="css/site.css" rel="stylesheet" />
</head>
<body>
    <component type="typeof(App)" render-mode="ServerPrerendered" />
    <div id="blazor-error-ui">
        <environment include="Staging,Production">
            An error has occurred. This application may no longer respond until reloaded.
        </environment>
        <environment include="Development">
            An unhandled exception has occurred. See browser dev tools for details.
        </environment>
        <a href="" class="reload">Reload</a>
        <a class="dismiss">🗙</a>
    </div>
    <script src="_framework/blazor.server.js"></script>
</body>
</html>
```

**验证**：`cat IoTHunter.WebUI/Pages/_Host.cshtml` 确认 `<base href="~/" />` 和 Bootstrap CDN 存在。☐

---

### 1.7 实现 UI 页面组件（V8.0：线程安全修复 + 错误处理增强）

**操作**：依次修改以下文件，每修改一个后 `dotnet build` 验证。

#### 1.7.1 登录页面

完全替换 `IoTHunter.WebUI/Pages/Index.razor` 为以下内容：

```razor
@page "/"
@using IoTHunter.WebUI.Services
@inject ManagementApiClient ManagementApi
@inject NavigationManager Navigation

<div class="container mt-5">
    <div class="row justify-content-center">
        <div class="col-md-4">
            <div class="card shadow">
                <div class="card-body">
                    <h3 class="card-title text-center mb-4">IoTHunter 运营控制台</h3>
                    <div class="mb-3">
                        <label class="form-label">用户名</label>
                        <input @bind="username" class="form-control" placeholder="请输入用户名" />
                    </div>
                    <div class="mb-3">
                        <label class="form-label">密码</label>
                        <input @bind="password" type="password" class="form-control" placeholder="请输入密码" />
                    </div>
                    @if (isLoading)
                    {
                        <button class="btn btn-primary w-100" disabled>
                            <span class="spinner-border spinner-border-sm me-2"></span>登录中...
                        </button>
                    }
                    else
                    {
                        <button class="btn btn-primary w-100" @onclick="Login">登录</button>
                    }
                    @if (!string.IsNullOrEmpty(error))
                    {
                        <div class="alert alert-danger mt-3">@error</div>
                    }
                </div>
            </div>
        </div>
    </div>
</div>

@code {
    private string username = "admin";
    private string password = "admin123";
    private string? error;
    private bool isLoading;

    private async Task Login()
    {
        isLoading = true;
        error = null;
        try
        {
            var success = await ManagementApi.LoginAsync(username, password);
            if (success)
            {
                Navigation.NavigateTo("/dashboard");
            }
            else
            {
                error = "登录失败，请检查用户名和密码";
            }
        }
        catch (Exception ex)
        {
            error = $"连接失败: {ex.Message}";
        }
        finally
        {
            isLoading = false;
        }
    }
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

#### 1.7.2 主控制台页面（V8.0：InvokeAsync 线程安全修复；**P10-01** 快照 `LatencyP99Ms`；（**P10-05**）`SimulatorSnapshot` 无持续时间字段，本节不展示 `DurationSeconds`）

新建 `IoTHunter.WebUI/Pages/Dashboard.razor`，粘贴以下完整内容：

```razor
@page "/dashboard"
@using IoTHunter.WebUI.Services
@inject SimulatorApiClient SimulatorApi
@inject NavigationManager Navigation
@implements IDisposable

<div class="container-fluid">
    <nav class="navbar navbar-expand-lg navbar-dark bg-dark rounded mb-4">
        <div class="container-fluid">
            <span class="navbar-brand">IoTHunter 运营控制台</span>
            <div class="navbar-nav">
                <a class="nav-link" href="/dashboard">监控大屏</a>
                <a class="nav-link" href="/devices">设备管理</a>
                <a class="nav-link" href="/traces">链路追踪</a>
                <a class="nav-link" href="/loadtest">压测指挥中心</a>
            </div>
        </div>
    </nav>

    @if (error != null)
    {
        <div class="alert alert-danger">@error</div>
    }

    <div class="row mt-4">
        <div class="col-md-3">
            <div class="card text-white bg-primary shadow">
                <div class="card-body text-center">
                    <h5>在线设备</h5>
                    <h2>@deviceCount</h2>
                </div>
            </div>
        </div>
        <div class="col-md-3">
            <div class="card text-white bg-success shadow">
                <div class="card-body text-center">
                    <h5>当前 QPS</h5>
                    <h2>@qps.ToString("F1")</h2>
                </div>
            </div>
        </div>
        <div class="col-md-3">
            <div class="card text-white bg-info shadow">
                <div class="card-body text-center">
                    <h5>成功率</h5>
                    <h2>@successRate.ToString("F2")%</h2>
                </div>
            </div>
        </div>
        <div class="col-md-3">
            <div class="card text-white bg-warning shadow">
                <div class="card-body text-center">
                    <h5>P99 延迟</h5>
                    <h2>@p99.ToString("F1") ms</h2>
                </div>
            </div>
        </div>
    </div>
</div>

@code {
    private double qps, successRate, p99;
    private int deviceCount;
    private string? error;
    private CancellationTokenSource? cts;

    protected override async Task OnInitializedAsync()
    {
        cts = new CancellationTokenSource();
        _ = PollSnapshotAsync(cts.Token);
    }

    private async Task PollSnapshotAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snapshot = await SimulatorApi.GetSnapshotAsync();
                var config = await SimulatorApi.GetConfigAsync();
                
                // V8.0 关键修正：必须在 UI 线程上更新状态
                await InvokeAsync(() =>
                {
                    if (snapshot is not null)
                    {
                        qps = snapshot.Qps;
                        successRate = snapshot.SuccessRate;
                        p99 = snapshot.LatencyP99Ms;
                    }
                    if (config is not null)
                    {
                        deviceCount = config.DeviceCount;
                    }
                    error = null;
                    StateHasChanged();
                });
            }
            catch (Exception ex)
            {
                await InvokeAsync(() =>
                {
                    error = $"数据获取失败: {ex.Message}";
                    StateHasChanged();
                });
            }
            await Task.Delay(2000, ct);
        }
    }

    public void Dispose()
    {
        cts?.Cancel();
        cts?.Dispose();
    }
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

#### 1.7.3 设备接入面板（V8.0：补全代码）

新建 `IoTHunter.WebUI/Pages/Devices.razor`，粘贴以下内容：

```razor
@page "/devices"
@using IoTHunter.WebUI.Services
@inject ManagementApiClient ManagementApi
@inject NavigationManager Navigation

<div class="container-fluid">
    <nav class="navbar navbar-expand-lg navbar-dark bg-dark rounded mb-4">
        <div class="container-fluid">
            <span class="navbar-brand">IoTHunter</span>
            <div class="navbar-nav">
                <a class="nav-link" href="/dashboard">监控大屏</a>
                <a class="nav-link active" href="/devices">设备管理</a>
                <a class="nav-link" href="/traces">链路追踪</a>
                <a class="nav-link" href="/loadtest">压测指挥中心</a>
            </div>
        </div>
    </nav>

    <h3>设备接入面板</h3>

    @if (error != null)
    {
        <div class="alert alert-danger">@error</div>
    }

    @if (gatewayStatus != null)
    {
        <div class="row mt-4">
            <div class="col-md-6">
                <div class="card">
                    <div class="card-header">网关配置</div>
                    <div class="card-body">
                        <pre class="bg-light p-3 rounded">@gatewayJson</pre>
                    </div>
                </div>
            </div>
            <div class="col-md-6">
                <div class="card">
                    <div class="card-header">设备默认凭证</div>
                    <div class="card-body">
                        <pre class="bg-light p-3 rounded">@credentialsJson</pre>
                    </div>
                </div>
            </div>
        </div>
    }
</div>

@code {
    private GatewayStatus? gatewayStatus;
    private string? error;
    private string gatewayJson = "";
    private string credentialsJson = "";

    protected override async Task OnInitializedAsync()
    {
        try
        {
            gatewayStatus = await ManagementApi.GetGatewayStatusAsync();
            if (gatewayStatus is not null)
            {
                gatewayJson = System.Text.Json.JsonSerializer.Serialize(gatewayStatus.Gateway,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                credentialsJson = System.Text.Json.JsonSerializer.Serialize(gatewayStatus.DefaultDeviceCredentials,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            }
        }
        catch (Exception ex)
        {
            error = $"获取网关状态失败: {ex.Message}";
        }
    }
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

#### 1.7.4 全链路追踪浏览器（V8.0：补全代码）

新建 `IoTHunter.WebUI/Pages/TraceViewer.razor`，粘贴以下内容：

```razor
@page "/traces"
@using IoTHunter.WebUI.Services
@inject BackendProcessorApiClient BackendApi
@inject NavigationManager Navigation

<div class="container-fluid">
    <nav class="navbar navbar-expand-lg navbar-dark bg-dark rounded mb-4">
        <div class="container-fluid">
            <span class="navbar-brand">IoTHunter</span>
            <div class="navbar-nav">
                <a class="nav-link" href="/dashboard">监控大屏</a>
                <a class="nav-link" href="/devices">设备管理</a>
                <a class="nav-link active" href="/traces">链路追踪</a>
                <a class="nav-link" href="/loadtest">压测指挥中心</a>
            </div>
        </div>
    </nav>

    <h3>全链路追踪浏览器</h3>

    <div class="row mt-3">
        <div class="col-md-4">
            <div class="input-group">
                <input @bind="searchDeviceId" class="form-control" placeholder="输入设备 ID（如 demo-http）" />
                <button class="btn btn-primary" @onclick="SearchTraces" disabled="@isLoading">
                    @(isLoading ? "搜索中..." : "搜索")
                </button>
            </div>
        </div>
    </div>

    @if (!string.IsNullOrEmpty(error))
    {
        <div class="alert alert-danger mt-3">@error</div>
    }

    @if (traces != null && traces.Count > 0)
    {
        <div class="mt-4">
            <h5>找到 @traces.Count 条 Trace</h5>
            @foreach (var trace in traces)
            {
                <div class="card mt-2">
                    <div class="card-header">
                        Trace ID: <code>@trace.TraceId</code>
                    </div>
                    <div class="card-body">
                        <table class="table table-sm">
                            <thead>
                                <tr><th>操作</th><th>耗时 (ms)</th></tr>
                            </thead>
                            <tbody>
                                @foreach (var seg in trace.Segments)
                                {
                                    var color = seg.DurationMs > 100 ? "text-danger" : seg.DurationMs > 50 ? "text-warning" : "text-success";
                                    <tr>
                                        <td>@seg.OperationName</td>
                                        <td class="@color fw-bold">@seg.DurationMs.ToString("F1")</td>
                                    </tr>
                                }
                            </tbody>
                        </table>
                    </div>
                </div>
            }
        </div>
    }
    else if (traces != null)
    {
        <div class="alert alert-info mt-3">未找到该设备的 Trace 记录</div>
    }
</div>

@code {
    private string searchDeviceId = "demo-http";
    private List<TraceSummary>? traces;
    private string? error;
    private bool isLoading;

    private async Task SearchTraces()
    {
        isLoading = true;
        error = null;
        traces = null;
        try
        {
            traces = await BackendApi.GetTracesAsync(searchDeviceId);
        }
        catch (Exception ex)
        {
            error = $"查询失败: {ex.Message}";
        }
        finally
        {
            isLoading = false;
        }
    }
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

#### 1.7.5 压测指挥中心（V8.0：补全代码；**P10-01**：`TotalSent` / `TotalFailed` / `LatencyP99Ms` / `LatencyMaxMs`，无 `TotalSuccess`/`LatencyP99`/`P50`，Max 标签用 **Max**

新建 `IoTHunter.WebUI/Pages/LoadTest.razor`，粘贴以下内容：

```razor
@page "/loadtest"
@using IoTHunter.WebUI.Services
@inject SimulatorApiClient SimulatorApi
@inject NavigationManager Navigation
@implements IDisposable

<div class="container-fluid">
    <nav class="navbar navbar-expand-lg navbar-dark bg-dark rounded mb-4">
        <div class="container-fluid">
            <span class="navbar-brand">IoTHunter</span>
            <div class="navbar-nav">
                <a class="nav-link" href="/dashboard">监控大屏</a>
                <a class="nav-link" href="/devices">设备管理</a>
                <a class="nav-link" href="/traces">链路追踪</a>
                <a class="nav-link active" href="/loadtest">压测指挥中心</a>
            </div>
        </div>
    </nav>

    <h3>压测指挥中心</h3>

    @if (error != null)
    {
        <div class="alert alert-danger">@error</div>
    }

    @if (snapshot != null)
    {
        <div class="row mt-4">
            <div class="col-md-3">
                <div class="card text-white bg-primary shadow">
                    <div class="card-body text-center">
                        <h5>总请求</h5>
                        <h2>@(snapshot.TotalSent + snapshot.TotalFailed)</h2>
                    </div>
                </div>
            </div>
            <div class="col-md-3">
                <div class="card text-white bg-success shadow">
                    <div class="card-body text-center">
                        <h5>成功</h5>
                        <h2>@snapshot.TotalSent</h2>
                    </div>
                </div>
            </div>
            <div class="col-md-3">
                <div class="card text-white bg-danger shadow">
                    <div class="card-body text-center">
                        <h5>失败</h5>
                        <h2>@snapshot.TotalFailed</h2>
                    </div>
                </div>
            </div>
            <div class="col-md-3">
                <div class="card text-white bg-info shadow">
                    <div class="card-body text-center">
                        <h5>当前 QPS</h5>
                        <h2>@snapshot.Qps.ToString("F1")</h2>
                    </div>
                </div>
            </div>
        </div>
        <div class="row mt-4">
            <div class="col-md-6">
                <div class="card">
                    <div class="card-header">延迟分布</div>
                    <div class="card-body">
                        <table class="table table-sm">
                            <tr><td>P99</td><td class="fw-bold">@snapshot.LatencyP99Ms.ToString("F1") ms</td></tr>
                            <tr><td>Max</td><td>@snapshot.LatencyMaxMs.ToString("F1") ms</td></tr>
                        </table>
                    </div>
                </div>
            </div>
            <div class="col-md-6">
                <div class="card">
                    <div class="card-header">压测配置（来自 Simulator /config）</div>
                    <div class="card-body text-center">
                        <h2>@(config?.DurationSeconds ?? 0) 秒</h2>
                        <p class="text-muted small mb-0">IntervalMs=@(config?.IntervalMs ?? 0) · Concurrency=@(config?.Concurrency ?? 0)</p>
                    </div>
                </div>
            </div>
        </div>
        <div class="mt-2 text-muted">数据每 2 秒自动刷新</div>
    }
    else
    {
        <div class="alert alert-info">等待 Simulator 数据...</div>
    }
</div>

@code {
    private SimulatorSnapshot? snapshot;
    private SimulatorConfig? config;
    private string? error;
    private CancellationTokenSource? cts;

    protected override async Task OnInitializedAsync()
    {
        cts = new CancellationTokenSource();
        _ = PollLoopAsync(cts.Token);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snap = await SimulatorApi.GetSnapshotAsync();
                var cfg = await SimulatorApi.GetConfigAsync();
                await InvokeAsync(() =>
                {
                    snapshot = snap;
                    config = cfg;
                    error = null;
                    StateHasChanged();
                });
            }
            catch (Exception ex)
            {
                await InvokeAsync(() =>
                {
                    error = $"数据获取失败: {ex.Message}";
                    StateHasChanged();
                });
            }
            await Task.Delay(2000, ct);
        }
    }

    public void Dispose()
    {
        cts?.Cancel();
        cts?.Dispose();
    }
}
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

---

### 1.8 配置 Program.cs（V8.0：`AuthTokenHolder` + 命名 HttpClient + 环境变量）

**操作**：完全替换 `IoTHunter.WebUI/Program.cs`，粘贴以下内容：

```csharp
using IoTHunter.WebUI.Services;
using IoTHunter.Infrastructure;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) =>
    LoggingDefaults.ConfigureBaseLogger(configuration));

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// Scoped：与 Blazor Server 线路一致，保存登录后的 JWT（见 §1.4.1 ManagementApiClient）
builder.Services.AddScoped<AuthTokenHolder>();

builder.Services.AddHttpClient("Management", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ApiEndpoints:ManagementBaseUrl"]!);
});

builder.Services.AddScoped<ManagementApiClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var http = factory.CreateClient("Management");
    return new ManagementApiClient(http, sp.GetRequiredService<AuthTokenHolder>());
});

builder.Services.AddHttpClient<BackendProcessorApiClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ApiEndpoints:BackendProcessorBaseUrl"]!);
});

builder.Services.AddHttpClient<SimulatorApiClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ApiEndpoints:DeviceSimulatorBaseUrl"]!);
});

// P10-04：未接入 BackendProcessor MonitoringHub 前请勿注册 MonitoringService。
// builder.Services.AddSingleton<MonitoringService>();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
```

**验证**：`dotnet build` 0 Error, 0 Warning。☐

#### 1.8.1 WebUI 专属 NetworkPolicy（**P-F1 第 10 步**：三条完整模板）

**操作**：在 **第 6 步** 已应用 `default-deny-all` 及业务 Egress 白名单的命名空间（如 `iothunter`）内，为 WebUI Deployment Pod 打上 `metadata.labels.app: iothunter-webui`（或与下表一致），新建 `deploy/webui-network-policy.yaml`，粘贴以下 **三条** `NetworkPolicy` 后执行 `kubectl apply -f deploy/webui-network-policy.yaml -n iothunter`。

**说明**：**Ingress Controller → WebUI**（从集群外经 Ingress 访问）依赖各发行版 `namespaceSelector`/`podSelector` 差异较大，**不设固定模板**；本节仅落实 **WebUI 出站**与 **入站到 Management / BackendProcessor / DeviceSimulator** 的最小集（与第 6 步 §1.8 P-F1 一致）。若外网无法打开页面，再按现场 Ingress Controller 标签补写第四条 **allow-ingress-controller-to-webui**。

```yaml
# 1) WebUI → Management / BackendProcessor / DeviceSimulator + kube-dns
---
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: allow-webui-egress
  namespace: iothunter
spec:
  podSelector:
    matchLabels:
      app: iothunter-webui
  policyTypes:
    - Egress
  egress:
    - to:
        - podSelector:
            matchLabels:
              app: iothunter-management
      ports:
        - protocol: TCP
          port: 80
    - to:
        - podSelector:
            matchLabels:
              app: backend-processor
      ports:
        - protocol: TCP
          port: 80
        - protocol: TCP
          port: 9464
    - to:
        - podSelector:
            matchLabels:
              app: device-simulator
      ports:
        - protocol: TCP
          port: 80
        - protocol: TCP
          port: 9464
    - to:
        - namespaceSelector: {}
          podSelector:
            matchLabels:
              k8s-app: kube-dns
      ports:
        - protocol: UDP
          port: 53
        - protocol: TCP
          port: 53
---
# 2) Management：仅接受来自 WebUI 的 HTTP
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: allow-webui-to-management
  namespace: iothunter
spec:
  podSelector:
    matchLabels:
      app: iothunter-management
  policyTypes:
    - Ingress
  ingress:
    - from:
        - podSelector:
            matchLabels:
              app: iothunter-webui
      ports:
        - protocol: TCP
          port: 80
---
# 3) BackendProcessor + DeviceSimulator：接受来自 WebUI 的 HTTP / Prometheus 端口（与 Simulator 双端口监听一致时可收紧为单端口）
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: allow-webui-to-backends
  namespace: iothunter
spec:
  podSelector:
    matchExpressions:
      - key: app
        operator: In
        values:
          - backend-processor
          - device-simulator
  policyTypes:
    - Ingress
  ingress:
    - from:
        - podSelector:
            matchLabels:
              app: iothunter-webui
      ports:
        - protocol: TCP
          port: 80
        - protocol: TCP
          port: 9464
```

**验证**：WebUI Pod 就绪后，`curl`/浏览器经业务路径可访问 Management 聚合数据与 Simulator 快照；从**无标签**的临时 Pod 直连 `backend-processor:80` 仍应被策略拒绝（若第 6 步 `default-deny-all` 仍生效）。☐

---

### 1.9 编写 Dockerfile（安装 curl）

**操作**：新建 `IoTHunter.WebUI/Dockerfile`，粘贴以下内容：

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish IoTHunter.WebUI/IoTHunter.WebUI.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80
ENTRYPOINT ["dotnet", "IoTHunter.WebUI.dll"]
```

**验证**：`docker build -t iothunter-webui:step10 -f IoTHunter.WebUI/Dockerfile .` 成功。☐

---

## 2. Live E2E 验证剧本（V8.0 增强版）

> **前置条件**：所有后端服务运行中，Management 鉴权已启用。

| 编号 | 验证场景         | 操作                                  | 预期结果                                                | 通过 |
| ---- | ---------------- | ------------------------------------- | ------------------------------------------------------- | ---- |
| 10.1 | 编译             | `dotnet build`                        | 0 Error, 0 Warning                                      | ☐    |
| 10.2 | 登录页面         | 浏览器打开 `http://localhost:5083`    | 显示深色主题登录表单，含用户名/密码输入框               | ☐    |
| 10.3 | 登录成功跳转     | 输入 `admin/admin123`，点击登录       | 跳转至 `/dashboard`，浏览器 URL 变化                    | ☐    |
| 10.4 | 实时监控大屏     | 查看 `/dashboard`                     | 四张彩色卡片实时刷新，显示 QPS、成功率、P99、设备数     | ☐    |
| 10.5 | 设备接入面板     | 点击导航“设备管理”                    | 显示网关配置 JSON 和设备默认凭证信息                    | ☐    |
| 10.6 | 全链路追踪浏览器 | 点击“链路追踪”，输入 `demo-http` 搜索 | 显示 Trace 表格，含操作名和分段耗时（颜色标识）         | ☐    |
| 10.7 | 压测指挥中心     | 点击“压测指挥中心”                    | **P10-01**：卡片为 `TotalSent + TotalFailed`、`TotalSent`、`TotalFailed`；延迟表为 `LatencyP99Ms` 与 **Max**（`LatencyMaxMs`）；配置区可含 `config.DurationSeconds`（来自 `/config`，非 snapshot） |
| 10.8 | 鉴权验证         | 直接访问 `/devices` 或重开浏览器      | 页面正常显示数据（登录后 Token 保存在 Scoped `AuthTokenHolder`，后续请求附带 Bearer） | ☐    |

---

## 3. 人工测试操作指南

### 3.1 环境准备

确保所有后端服务运行中：

```bash
curl -s http://localhost:5080/health/ready   # IoTGateway
curl -s http://localhost:5081/health/ready   # BackendProcessor
curl -s http://localhost:5091/api/simulator/health  # DeviceSimulator
curl -s http://localhost:5082/health/ready   # IoTHunter.Management
```

### 3.2 启动 UI

```bash
cd IoTHunter
dotnet run --project IoTHunter.WebUI/IoTHunter.WebUI.csproj
```

**预期**：控制台输出 `Now listening on: http://localhost:5083`。☐

### 3.3 逐项验证

浏览器打开 `http://localhost:5083`。

#### 测试 10.2：登录页面

**预期**：深色主题登录表单，含用户名（默认 `admin`）和密码（默认 `admin123`）输入框，以及蓝色登录按钮。  
**☐ 通过。** 截图保存 `screenshots/step10/01-login.png`。

#### 测试 10.3：登录成功跳转

点击登录按钮。  
**预期**：页面跳转至 `/dashboard`，URL 变为 `http://localhost:5083/dashboard`，无错误提示。  
**☐ 通过。**

#### 测试 10.4：实时监控大屏

停留 10 秒观察 `/dashboard` 页面。  
**预期**：四张彩色卡片（在线设备数、当前QPS、成功率、P99延迟）的数字随时间变化，每 2 秒刷新一次。  
**☐ 通过。** 截图保存 `screenshots/step10/02-dashboard.png`。

#### 测试 10.5：设备接入面板

点击导航栏“设备管理”。  
**预期**：显示网关配置（MQTT 监听地址、Kafka 集群等信息）和设备默认凭证（devicesim/devicesimp）。  
**☐ 通过。** 截图保存。

#### 测试 10.6：全链路追踪浏览器

点击“链路追踪”，在搜索框输入 `demo-http` 或 `dev-000001`，点击搜索。  
**预期**：显示 Trace 列表，每条 Trace 含操作名称和耗时，超过 100ms 的行显示为红色。  
**☐ 通过。** 截图保存。

#### 测试 10.7：压测指挥中心

点击“压测指挥中心”。  
**预期**：显示实时跳动数字（总发送/成功/失败/QPS），数值每 2 秒更新。  
**☐ 通过。** 截图保存。

#### 测试 10.8：鉴权验证

关闭浏览器，重新打开 `http://localhost:5083`，直接访问 `http://localhost:5083/devices`。  
**预期**：重定向到登录页面 `/`，需要重新登录。  
**☐ 通过。**

### 3.4 收尾

- 所有截图保存至 `screenshots/step10/`。
- 停止 UI：按 `Ctrl+C`。

---

## 4. 完成标准

- [ ] 登录认证与 Management 对接成功，JWT 经 Scoped `AuthTokenHolder` 传递，受保护接口请求携带 Bearer
- [ ] 实时监控大屏每 2 秒刷新，**无白屏崩溃**（InvokeAsync 线程安全）
- [ ] 全链路追踪浏览器可搜索设备 Trace，分段耗时用颜色区分
- [ ] 压测指挥中心实时显示 QPS、成功/失败数
- [ ] 设备接入面板展示网关状态
- [ ] **Bootstrap 5 深色主题商业界面展示正常**
- [ ] **错误状态有明确提示，而非静默失败**
- [ ] Live 验证 8 项全部通过
- [ ] 无硬编码后端地址（通过 appsettings.json + 环境变量注入）
- [ ] 无 Mock 数据
- [ ] Dockerfile 已安装 curl
- [ ] （K8s）已按需应用 §1.8.1 WebUI NetworkPolicy，与第 6 步零信任策略一致

---

第十步开发计划 V8.0 最终版完毕。已落实 **P10-02**（`AuthTokenHolder` + `ManagementApiClient` 逐请求 Bearer）、**P10-03/P10-01/P10-05**（DTO 与 `Dashboard`/`LoadTest` 字段）、**P10-04**（`MonitoringService` 占位与 `Program` 注册策略）、**P-F1**（§1.8.1 三条 WebUI NetworkPolicy）。至此，IoTHunter 全链路微服务物联网数据网关项目十步开发计划全部完成。请定稿。