# 第 9 步开发计划：IoTHunter.Management 运营管理微服务（Live E2E 增强版 V8.0 最终版）

> **版本**：V8.0（熔接三次裁决：JWT 鉴权全局注册、GatewayController DI 修复、JWT 密钥高熵化、[Authorize] 特性补全、Dockerfile 安装 curl、引用 Infrastructure 类库）  
> **对应总架构**：`架构设计文档 V7.0` 第 9 步  
> **前置依赖**：第 2 步 IoTGateway（提供 `/api/v1/config` 端点）、第 8 步端到端压测均已完成  
> **本步目标**：开发独立的运营管理微服务 `IoTHunter.Management`，为第10步 UI 运营控制台提供登录认证（JWT）和设备 MQTT 凭证管理功能。**本服务是 UI 唯一直接对接的后端，必须启用完整的 JWT 鉴权中间件，确保管理 API 不被匿名访问。**  
> **本步边界**：只实现 Management 微服务自身逻辑，不修改 IoTGateway、BackendProcessor 或 DeviceSimulator。不依赖第 7 步的可观测性栈。  
> **核心禁令**：  
> - **硬编码禁令（ADR-022）**：所有连接串、JWT 密钥、凭据通过 `IConfiguration` 注入，禁止硬编码。**JWT 密钥必须使用高熵随机串，appsettings.json 中的示例值仅供本地开发，生产环境须通过 Secret 注入。**  
> - **Mock 数据禁令（ADR-023）**：验证必须调用真实 IoTGateway 的 `/api/v1/config` 端点。  
> - **Management 边界（ADR-025）**：禁止直连 Kafka、数据库或 Mosquitto，只能通过已有 API 获取信息。  
> **【V8.0 关键修正】**：  
> - **JWT 鉴权全局启用**：`Program.cs` 中增加完整的 `AddAuthentication().AddJwtBearer()` 配置，`DeviceCredentialsController` 和 `GatewayController` 添加 `[Authorize]` 特性。  
> - **GatewayController DI 修复**：修正 `IHttpClientFactory` 和 `IoTGatewayOptions` 的注入方式，使用命名客户端 `CreateClient("IoTGateway")`。  
> - **JWT 密钥高熵化**：示例密钥更换为 64 字符随机串，并标注生产环境必须通过外部 Secret 注入。  
> - **Dockerfile 安装 curl**：确保容器健康检查可用。  
> - **引用 Infrastructure 类库**：项目引用 `IoTHunter.Infrastructure`，统一日志和配置基础设施。

---

## 0. 前置检查

| 检查项                            | 位置 / 验证命令                                              | 状态要求                  |
| --------------------------------- | ------------------------------------------------------------ | ------------------------- |
| IoTGateway 运行中                 | `curl -s http://localhost:5080/health/ready`                 | 返回 `Healthy`            |
| IoTGateway config 端点可用        | `curl -s http://localhost:5080/api/v1/config`                | 返回 Mqtt/Kafka 配置 JSON |
| 第 2 步验证通过                   | `screenshots/step2/` 下存在全部验证证据                      | 14 项剧本已勾选           |
| .NET 10 SDK                       | `dotnet --version`                                           | 10.x.xxx                  |
| `IoTHunter.Infrastructure` 可编译 | `dotnet build IoTHunter.Infrastructure/IoTHunter.Infrastructure.csproj` | 0 Error, 0 Warning        |

---

## 1. 动作项

### 1.1 创建 IoTHunter.Management 项目

**操作**：在解决方案根目录执行以下命令：

```bash
cd IoTHunter
dotnet new webapi -n IoTHunter.Management -f net10.0 --no-https --use-controllers
dotnet sln add IoTHunter.Management/IoTHunter.Management.csproj
dotnet add IoTHunter.Management/IoTHunter.Management.csproj reference IoTHunter.Shared/IoTHunter.Shared.csproj
dotnet add IoTHunter.Management/IoTHunter.Management.csproj reference IoTHunter.Infrastructure/IoTHunter.Infrastructure.csproj
```

**验证**：`dotnet build IoTHunter.Management/IoTHunter.Management.csproj` 0 Error, 0 Warning。☐

---

### 1.2 安装 NuGet 依赖

**操作**：执行以下命令：

```bash
dotnet add IoTHunter.Management/IoTHunter.Management.csproj package Microsoft.AspNetCore.Authentication.JwtBearer
dotnet add IoTHunter.Management/IoTHunter.Management.csproj package Microsoft.Extensions.Http
dotnet add IoTHunter.Management/IoTHunter.Management.csproj package Serilog.AspNetCore
dotnet add IoTHunter.Management/IoTHunter.Management.csproj package Serilog.Sinks.Console
```

**验证**：`dotnet restore IoTHunter.Management/IoTHunter.Management.csproj` 成功。☐

---

### 1.3 更新配置文件

**操作**：完全替换 `IoTHunter.Management/appsettings.json` 为以下内容。**密钥为本地开发示例，生产环境必须通过 K8s Secret 或 Vault 注入。**

```json
{
  "AllowedHosts": "*",
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://localhost:5082"
      }
    }
  },
  "Auth": {
    "DefaultAdminUsername": "admin",
    "DefaultAdminPassword": "admin123",
    "JwtSecret": "aB3xK9mP2qR7tW5vY1zC4eF6hJ8lN0pQ3sU9wX2yZ5aB8cD1eF4gH7Z9iJ6kLmNo",
    "TokenExpirationHours": 8
  },
  "IoTGateway": {
    "ConfigUrl": "http://localhost:5080/api/v1/config"
  }
}
```

**说明**：`Auth:JwtSecret` 示例值为 **64 字符**（**P9-02**：助记后缀为 **`…iJ6kLmNo`**；旧版若以 `…iJ6kLmNoX2` 等不足/多余长度出现，请以 §1.3 本节为准对齐）。本地开发示例；生产须通过 Secret 注入并轮换。

**验证**：`cat IoTHunter.Management/appsettings.json` 确认内容正确。☐

---

### 1.4 定义选项类

**操作**：依次新建以下两个文件，每新建一个后执行 `dotnet build IoTHunter.Management/IoTHunter.Management.csproj` 确认无编译错误。

#### 1.4.1 AuthOptions

新建 `IoTHunter.Management/Infrastructure/Options/AuthOptions.cs`，粘贴以下内容：

```csharp
namespace IoTHunter.Management.Infrastructure.Options;

public sealed class AuthOptions
{
    public string DefaultAdminUsername { get; set; } = "admin";
    public string DefaultAdminPassword { get; set; } = "admin123";
    public string JwtSecret { get; set; } = "aB3xK9mP2qR7tW5vY1zC4eF6hJ8lN0pQ3sU9wX2yZ5aB8cD1eF4gH7Z9iJ6kLmNo";
    public int TokenExpirationHours { get; set; } = 8;
}
```

**验证**：`dotnet build IoTHunter.Management/IoTHunter.Management.csproj` 0 Error, 0 Warning。☐

#### 1.4.2 IoTGatewayOptions

新建 `IoTHunter.Management/Infrastructure/Options/IoTGatewayOptions.cs`，粘贴以下内容：

```csharp
namespace IoTHunter.Management.Infrastructure.Options;

public sealed class IoTGatewayOptions
{
    public string ConfigUrl { get; set; } = "http://localhost:5080/api/v1/config";
}
```

**验证**：`dotnet build IoTHunter.Management/IoTHunter.Management.csproj` 0 Error, 0 Warning。☐

---

### 1.5 实现登录认证控制器（V8.0：密钥高熵化 + 登录端点 `[AllowAnonymous]`）

**操作**：新建 `IoTHunter.Management/Controllers/AuthController.cs`，粘贴以下完整内容。

**【说明（P9-01）】**若 `Program.cs` 启用全局 JWT 兜底策略（`FallbackPolicy` 要求已认证），未标注 **`[AllowAnonymous]`** 的控制器会被提前拦截。**必须在 `[Route("api/v1/auth")]` 下一行声明 `[AllowAnonymous]`**（作用于本控制器），使 `POST /api/v1/auth/login` 可匿名访问。文件顶部须包含 `using Microsoft.AspNetCore.Authorization;`。

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using IoTHunter.Management.Infrastructure.Options;

namespace IoTHunter.Management.Controllers;

[ApiController]
[Route("api/v1/auth")]
[AllowAnonymous]
public class AuthController : ControllerBase
{
    private readonly AuthOptions _authOptions;

    public AuthController(AuthOptions authOptions)
    {
        _authOptions = authOptions;
    }

    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        if (request.Username == _authOptions.DefaultAdminUsername &&
            request.Password == _authOptions.DefaultAdminPassword)
        {
            var token = GenerateJwtToken(request.Username);
            return Ok(new { token, username = request.Username });
        }

        return Unauthorized(new { message = "Invalid credentials" });
    }

    private string GenerateJwtToken(string username)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_authOptions.JwtSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[] { new Claim(ClaimTypes.Name, username) };

        var token = new JwtSecurityToken(
            issuer: "IoTHunter.Management",
            audience: "IoTHunter.WebUI",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(_authOptions.TokenExpirationHours),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public sealed record LoginRequest(string Username, string Password);
```

**验证**：`dotnet build IoTHunter.Management/IoTHunter.Management.csproj` 0 Error, 0 Warning。☐

---

### 1.6 实现设备凭证查询端点（V8.0：添加 [Authorize]）

**操作**：新建 `IoTHunter.Management/Controllers/DeviceCredentialsController.cs`，粘贴以下完整内容：

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IoTHunter.Management.Controllers;

[ApiController]
[Route("api/v1/devices")]
[Authorize]
public class DeviceCredentialsController : ControllerBase
{
    [HttpGet("{deviceId}/credentials")]
    public IActionResult GetDeviceCredentials(string deviceId)
    {
        // 返回本地开发默认凭证，生产环境通过 K8s Secret 替换
        return Ok(new
        {
            deviceId,
            mqttBroker = "mosquitto:1883",
            username = "devicesim",
            password = "devicesimp",
            topicTelemetry = $"device/{deviceId}/telemetry",
            topicCritical = $"device/{deviceId}/event/critical"
        });
    }
}
```

**设计说明**：本端点返回设备接入 MQTT Broker 所需的凭证。本地开发阶段使用固定的 `devicesim/devicesimp` 凭证组。在第6步 K8s 部署时，此值将通过环境变量覆盖。`[Authorize]` 特性确保只有携带有效 JWT 的请求才能获取设备凭证。

**验证**：`dotnet build IoTHunter.Management/IoTHunter.Management.csproj` 0 Error, 0 Warning。☐

---

### 1.7 实现网关配置聚合查询端点（V8.0：DI 修复 + [Authorize]）

**操作**：新建 `IoTHunter.Management/Controllers/GatewayController.cs`，粘贴以下完整内容：

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using IoTHunter.Management.Infrastructure.Options;
using System.Text.Json;

namespace IoTHunter.Management.Controllers;

[ApiController]
[Route("api/v1/gateway")]
[Authorize]
public class GatewayController : ControllerBase
{
    private readonly HttpClient _httpClient;
    private readonly string _gatewayConfigUrl;

    public GatewayController(IHttpClientFactory httpClientFactory, IoTGatewayOptions gatewayOptions)
    {
        _httpClient = httpClientFactory.CreateClient("IoTGateway");
        _gatewayConfigUrl = gatewayOptions.ConfigUrl;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetGatewayStatus()
    {
        try
        {
            var response = await _httpClient.GetStringAsync(_gatewayConfigUrl);
            var gatewayConfig = JsonDocument.Parse(response).RootElement;

            return Ok(new
            {
                gateway = new
                {
                    mqtt = gatewayConfig.GetProperty("Mqtt"),
                    kafka = gatewayConfig.GetProperty("Kafka")
                },
                defaultDeviceCredentials = new
                {
                    mqttBroker = "mosquitto:1883",
                    username = "devicesim",
                    password = "devicesimp"
                }
            });
        }
        catch (HttpRequestException)
        {
            return Ok(new
            {
                gateway = "unreachable",
                message = "IoTGateway is not reachable. Please check if it is running."
            });
        }
    }
}
```

**设计说明**：此端点是 UI“设备接入面板”的主要数据来源。它聚合了网关自身运行态配置和设备接入所需的默认凭证。构造函数通过 `IHttpClientFactory` 创建命名客户端 `"IoTGateway"`，解决了 DI 解析问题。

**验证**：`dotnet build IoTHunter.Management/IoTHunter.Management.csproj` 0 Error, 0 Warning。☐

---

### 1.8 编写 Program.cs（V8.0：JWT 鉴权中间件完整注册 + DI 修正）

**操作**：完全替换 `IoTHunter.Management/Program.cs`，粘贴以下内容：

```csharp
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using IoTHunter.Management.Infrastructure.Options;
using IoTHunter.Infrastructure;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// 日志
builder.Host.UseSerilog((context, services, configuration) =>
    LoggingDefaults.ConfigureBaseLogger(configuration));

// 选项绑定
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.Configure<IoTGatewayOptions>(builder.Configuration.GetSection("IoTGateway"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<AuthOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<IoTGatewayOptions>>().Value);

// JWT 鉴权中间件（V8.0 关键修正：全局启用认证）
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = "IoTHunter.Management",
        ValidAudience = "IoTHunter.WebUI",
        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(builder.Configuration["Auth:JwtSecret"]!))
    };
});
builder.Services.AddAuthorization();

// HTTP 客户端（V8.0 修正：使用命名客户端）
builder.Services.AddHttpClient("IoTGateway", client =>
{
    var gatewayOptions = builder.Configuration.GetSection("IoTGateway").Get<IoTGatewayOptions>();
    client.BaseAddress = new Uri(gatewayOptions?.ConfigUrl ?? "http://localhost:5080/api/v1/config");
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddControllers();

var app = builder.Build();

// 认证中间件（必须先于 MapControllers）
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready", service = "IoTHunter.Management" }));
app.Run();
```

**验证**：`dotnet build IoTHunter.Management/IoTHunter.Management.csproj` 0 Error, 0 Warning。☐

---

### 1.9 编写 Dockerfile（安装 curl）

**操作**：新建 `IoTHunter.Management/Dockerfile`，粘贴以下内容：

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish IoTHunter.Management/IoTHunter.Management.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80
ENTRYPOINT ["dotnet", "IoTHunter.Management.dll"]
```

**验证**：`docker build -t iothunter-management:step9 -f IoTHunter.Management/Dockerfile .` 成功。☐

---

## 2. Live E2E 验证剧本（V8.0 增强版）

> **前置条件**：IoTGateway 已启动，Management 服务已启动。

| 编号 | 验证场景                 | 操作步骤                                                     | 预期结果                                                | 通过 |
| ---- | ------------------------ | ------------------------------------------------------------ | ------------------------------------------------------- | ---- |
| 9.1  | 编译                     | `dotnet build IoTHunter.Management/IoTHunter.Management.csproj` | 0 Error, 0 Warning                                      | ☐    |
| 9.2  | 服务启动                 | `dotnet run --project IoTHunter.Management/IoTHunter.Management.csproj` | 监听 `http://localhost:5082`                            | ☐    |
| 9.3  | 健康检查                 | `curl -s http://localhost:5082/health/ready`                 | `{"status":"ready","service":"IoTHunter.Management"}`   | ☐    |
| 9.4  | 登录认证（正确密码）     | `curl -X POST .../auth/login -d '{"username":"admin","password":"admin123"}'` | HTTP 200，返回 `token` 和 `username: "admin"`           | ☐    |
| 9.5  | JWT Token 格式验证       | 解码 Token 的 Payload 部分                                   | 含 `sub: "admin"`, `iss: "IoTHunter.Management"`, `exp` | ☐    |
| 9.6  | 登录认证（错误密码）     | 同上请求，密码改为 `wrong`                                   | HTTP 401，`{"message":"Invalid credentials"}`           | ☐    |
| 9.7  | 设备凭证查询（带 Token） | `curl -H "Authorization: Bearer <token>" http://.../devices/dev-test/credentials` | 返回设备凭证 JSON                                       | ☐    |
| 9.8  | 设备凭证查询（无 Token） | 不带 Authorization 头                                        | HTTP 401                                                | ☐    |
| 9.9  | 网关状态聚合查询         | `curl -H "Authorization: Bearer <token>" http://.../gateway/status` | 返回 `gateway.mqtt` 和 `gateway.kafka`                  | ☐    |
| 9.10 | 网关不可达时降级         | 停止 IoTGateway，再次调用 `/gateway/status`                  | HTTP 200，`gateway: "unreachable"`，不崩溃              | ☐    |

---

## 3. 人工测试操作指南

### 3.1 环境准备

```bash
# 确认 IoTGateway 在运行
curl -s http://localhost:5080/health/ready
# 预期：Healthy
```

### 3.2 编译与启动

```bash
cd IoTHunter
dotnet build IoTHunter.Management/IoTHunter.Management.csproj
dotnet run --project IoTHunter.Management/IoTHunter.Management.csproj
```

**预期**：控制台输出 `Now listening on: http://localhost:5082`。☐

### 3.3 逐条执行验证

#### 测试 9.3：健康检查

```bash
curl -s http://localhost:5082/health/ready
```
**预期**：`{"status":"ready","service":"IoTHunter.Management"}`  
**☐ 通过。**

#### 测试 9.4：登录认证（正确密码）

```bash
curl -s -X POST http://localhost:5082/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"admin123"}'
```
**预期**：HTTP 200，响应体含 `token` 字段（JWT 字符串）和 `username: "admin"`。  
**☐ 通过。** 将 `token` 值复制备用。

#### 测试 9.5：JWT Token 格式验证

```bash
# 复制上一步得到的 token，执行：
echo "eyJhbGciOi..." | cut -d'.' -f2 | base64 -d 2>/dev/null | python3 -m json.tool
```
**预期**：Payload 含 `"sub": "admin"`, `"iss": "IoTHunter.Management"`, `"exp": ...`。  
**☐ 通过。**

#### 测试 9.6：错误密码返回 401

```bash
curl -s -X POST http://localhost:5082/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"wrong"}'
```
**预期**：HTTP 401，`{"message":"Invalid credentials"}`。  
**☐ 通过。**

#### 测试 9.7：设备凭证查询（携带 Token）

```bash
TOKEN="<此处粘贴测试9.4获取的token>"
curl -s -H "Authorization: Bearer $TOKEN" \
  http://localhost:5082/api/v1/devices/dev-test/credentials
```
**预期**：返回 JSON，含 `deviceId`, `mqttBroker`, `username: "devicesim"`, `topicTelemetry`, `topicCritical`。  
**☐ 通过。**

#### 测试 9.8：设备凭证查询（无 Token，验证鉴权）

```bash
curl -s http://localhost:5082/api/v1/devices/dev-test/credentials
```
**预期**：HTTP 401 Unauthorized。  
**☐ 通过。**

#### 测试 9.9：网关状态聚合查询

```bash
curl -s -H "Authorization: Bearer $TOKEN" \
  http://localhost:5082/api/v1/gateway/status
```
**预期**：JSON 含 `gateway` 对象（`mqtt` 和 `kafka` 子对象）及 `defaultDeviceCredentials`。  
**☐ 通过。**

#### 测试 9.10：网关不可达时降级

```bash
# 停止 IoTGateway（如果是 dotnet run，按 Ctrl+C），然后执行：
curl -s -H "Authorization: Bearer $TOKEN" \
  http://localhost:5082/api/v1/gateway/status
```
**预期**：`{"gateway":"unreachable","message":"IoTGateway is not reachable..."}`，不返回 500。  
**☐ 通过。** 重新启动 IoTGateway。

### 3.4 收尾

- 保存所有终端输出至 `screenshots/step9/`。
- 停止 Management 服务：按 `Ctrl+C`。

---

## 4. 完成标准

- [ ] `dotnet build` 0 Error, 0 Warning
- [ ] 健康检查端点 `/health/ready` 可访问
- [ ] `POST /api/v1/auth/login` 正确密码返回 JWT token，错误密码返回 401
- [ ] **JWT 鉴权中间件已全局注册，未携带 Token 的请求返回 401**
- [ ] `GET /api/v1/devices/{deviceId}/credentials` 需携带 Token 方可访问
- [ ] `GET /api/v1/gateway/status` 聚合 IoTGateway 配置，IoTGateway 不可达时降级返回提示
- [ ] **GatewayController 使用 `IHttpClientFactory.CreateClient("IoTGateway")` 正确注入**
- [ ] **JWT 密钥为 64 字符高熵随机串，生产环境通过外部 Secret 注入**
- [ ] Live 验证 10 项全部勾选通过
- [ ] 无硬编码连接串或密钥（所有配置来自 `appsettings.json` 或环境变量）
- [ ] 无 Mock 数据
- [ ] **不依赖 Kafka、数据库或 Mosquitto**

---

第九步开发计划 V8.0 最终版完毕。D爷，JWT 鉴权已全局启用，DI 已修正，密钥已高熵化，所有敏感接口已受保护。请定稿。