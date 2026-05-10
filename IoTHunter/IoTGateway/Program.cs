using IoTHunter.Infrastructure;
using IoTGateway.Infrastructure.Health;
using IoTGateway.Infrastructure.Messaging;
using IoTGateway.Infrastructure.Metrics;
using IoTGateway.Infrastructure.Mqtt;
using IoTGateway.Infrastructure.Options;
using IoTGateway.Infrastructure.Resilience;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// 日志
builder.Host.UseSerilog((context, services, configuration) =>
    LoggingDefaults.ConfigureBaseLogger(configuration));

// 选项绑定
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection("Kafka"));
builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<KafkaOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<MqttOptions>>().Value);

// 核心服务
builder.Services.AddSingleton<KafkaResiliencePipeline>();
builder.Services.AddSingleton<KafkaProducerService>();
builder.Services.AddSingleton<GatewayMetrics>();
builder.Services.AddSingleton<MqttIngestionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttIngestionService>());

// 健康检查
builder.Services.AddHealthChecks()
    .AddCheck<KafkaHealthCheck>("kafka", tags: ["external"])
    .AddCheck<MqttHealthCheck>("mqtt", tags: ["external"]);

// 后台服务异常不杀死宿主
builder.Services.Configure<HostOptions>(options =>
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

builder.Services.AddControllers();

// OpenTelemetry（注意：必须注册 Meter 和 ActivitySource）
builder.Services
    .AddIoTHunterOpenTelemetry(builder.Configuration, "IoTGateway")
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddMeter("IoTGateway")
        .AddPrometheusExporter());

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseOpenTelemetryPrometheusScrapingEndpoint();
app.MapControllers();
app.MapHealthChecks("/health/ready");
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.Run();
