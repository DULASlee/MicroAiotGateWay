using DeviceSimulator.Infrastructure;
using DeviceSimulator.Workers;
using IoTHunter.Infrastructure;
using OpenTelemetry.Metrics;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) =>
    LoggingDefaults.ConfigureBaseLogger(configuration));

var simOpts = builder.Configuration.GetSection("Simulator").Get<SimulatorOptions>()!;
builder.Services.AddSingleton(simOpts);

builder.Services.AddHttpClient("Gateway", client =>
{
    var proxyUrl = builder.Configuration["Simulator:ProxyUrl"];
    var httpBase = builder.Configuration["Simulator:GatewayHttpBase"] ?? "http://localhost:5080";
    client.BaseAddress = new Uri(string.IsNullOrWhiteSpace(proxyUrl) ? httpBase : proxyUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddSingleton<LoadTestMetrics>();

builder.Services
    .AddIoTHunterOpenTelemetry(builder.Configuration, "DeviceSimulator")
    .WithMetrics(metrics => metrics
        .AddMeter("DeviceSimulator")
        .AddPrometheusExporter());

builder.Services.AddHostedService<SimulatorWorker>();

var app = builder.Build();

app.UseRouting();
app.MapPrometheusScrapingEndpoint();
app.MapGet("/", () => "DeviceSimulator OK");
app.MapGet("/api/simulator/health", () => Results.Ok(new { status = "running" }));
app.MapGet("/api/simulator/config", (SimulatorOptions opts) => Results.Ok(new
{
    opts.Protocol,
    Target = opts.Protocol.Equals("mqtt", StringComparison.OrdinalIgnoreCase)
        ? opts.MqttWebSocketUrl
        : opts.GatewayHttpBase,
    opts.DeviceCount,
    opts.IntervalMs,
    opts.Concurrency,
    opts.DurationSeconds,
    opts.CriticalEventRatio
}));
app.MapGet("/api/simulator/snapshot", (LoadTestMetrics metrics) =>
    Results.Ok(metrics.GetSnapshot()));

await app.RunAsync();
