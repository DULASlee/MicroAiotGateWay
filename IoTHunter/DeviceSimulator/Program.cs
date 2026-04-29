using DeviceSimulator;
using DeviceSimulator.Infrastructure.Metrics;
using DeviceSimulator.Infrastructure.Options;
using IoTHunter.Shared.Infrastructure;
using OpenTelemetry.Metrics;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) =>
{
    LoggingDefaults.ConfigureBaseLogger(configuration);
});

// 标准 Options 绑定
builder.Services.Configure<SimulatorOptions>(
    builder.Configuration.GetSection("Simulator"));

// CLI 参数覆盖（PostConfigure 在 Configure 之后运行）
builder.Services.PostConfigure<SimulatorOptions>(opts =>
{
    var cmd = (string?)null;
    cmd = args.FirstOrDefault(a => a.StartsWith("--protocol="))?.Split('=')[1];
    if (cmd is not null) opts.Protocol = cmd;
    if (int.TryParse(args.FirstOrDefault(a => a.StartsWith("--devices="))?.Split('=')[1], out var dc)) opts.DeviceCount = dc;
    if (int.TryParse(args.FirstOrDefault(a => a.StartsWith("--interval="))?.Split('=')[1], out var iv)) opts.IntervalMs = iv;
    if (int.TryParse(args.FirstOrDefault(a => a.StartsWith("--concurrency="))?.Split('=')[1], out var cc)) opts.Concurrency = cc;
    if (int.TryParse(args.FirstOrDefault(a => a.StartsWith("--duration="))?.Split('=')[1], out var ds)) opts.DurationSeconds = ds;
});

// 命名 HttpClient：ProxyUrl 非空时走边缘代理，否则直连网关
var proxyUrl = builder.Configuration["Simulator:ProxyUrl"];
var httpBase = builder.Configuration["Simulator:GatewayHttpBase"]
               ?? "http://localhost:5080";
var effectiveBase = string.IsNullOrWhiteSpace(proxyUrl) ? httpBase : proxyUrl;
builder.Services.AddHttpClient("Gateway", client =>
{
    client.BaseAddress = new Uri(effectiveBase);
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddSingleton<SimulatorMetrics>();

builder.Services.AddIoTHunterOpenTelemetry(builder.Configuration, "DeviceSimulator")
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter("DeviceSimulator")
            .AddPrometheusExporter();
    });

builder.Services.AddHostedService<SimulatorWorker>();

var app = builder.Build();

app.UseRouting();
app.MapPrometheusScrapingEndpoint();
app.MapGet("/", () => "DeviceSimulator OK");

await app.RunAsync();
