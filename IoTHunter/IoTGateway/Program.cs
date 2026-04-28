using IoTHunter.Shared.Infrastructure;
using IoTGateway.Infrastructure.Health;
using IoTGateway.Infrastructure.Messaging;
using IoTGateway.Infrastructure.Metrics;
using IoTGateway.Infrastructure.Mqtt;
using IoTGateway.Infrastructure.Options;
using IoTGateway.Infrastructure.Resilience;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---- 1. 复用 Shared 日志基础设施 ----
builder.Host.UseSerilog((context, services, configuration) =>
{
    LoggingDefaults.ConfigureBaseLogger(configuration);
});

// ---- 2. 选项绑定 ----
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection("Kafka"));
builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IOptions<KafkaOptions>>().Value);
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IOptions<MqttOptions>>().Value);

// ---- 3. 核心服务注册 ----
builder.Services.AddSingleton(ResiliencePipelines.BuildKafkaPipeline());
builder.Services.AddSingleton<KafkaProducerService>();
builder.Services.AddSingleton<GatewayMetrics>();
builder.Services.AddSingleton<MqttIngestionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttIngestionService>());

// ---- 4. 健康检查 ----
builder.Services.AddHealthChecks()
    .AddCheck<KafkaHealthCheck>("kafka")
    .AddCheck<MqttHealthCheck>("mqtt");

// ---- 5. 控制器与严格 JSON ----
builder.Services.AddControllers();
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.ReadCommentHandling =
        System.Text.Json.JsonCommentHandling.Disallow;
    options.SerializerOptions.AllowTrailingCommas = false;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});

// ---- 6. 复用 Shared OTel 基础设施 + Gateway 独有扩展 ----
builder.Services
    .AddIoTHunterOpenTelemetry(builder.Configuration, "IoTGateway")
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation())
    .WithMetrics(metrics => metrics
        .AddMeter("IoTGateway")
        .AddAspNetCoreInstrumentation()
        .AddPrometheusExporter());

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseOpenTelemetryPrometheusScrapingEndpoint();
app.MapControllers();
app.MapHealthChecks("/health/ready");

app.Run();
