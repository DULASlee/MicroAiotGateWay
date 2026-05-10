using BackendProcessor.Infrastructure.Health;
using BackendProcessor.Infrastructure.Kafka;
using BackendProcessor.Infrastructure.Options;
using BackendProcessor.Hubs;
using BackendProcessor.Workers;
using IoTHunter.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Serilog
builder.Host.UseSerilog((context, services, configuration) =>
    LoggingDefaults.ConfigureBaseLogger(configuration));

// Data Sources (BP-1)
var csPg = builder.Configuration.GetConnectionString("Postgres")!;
builder.Services.AddSingleton(NpgsqlDataSource.Create(csPg));

var csRedis = builder.Configuration.GetConnectionString("Redis")!;
builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(csRedis));

var kafkaBootstrap = builder.Configuration["Kafka:BootstrapServers"]!;

// DLQ
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<KafkaDlqProducer>>();
    return new KafkaDlqProducer(kafkaBootstrap, logger);
});

// Consumers
builder.Services.AddSingleton(sp =>
{
    var section = builder.Configuration.GetSection("Kafka:Consumers:Persistence");
    var opts = section.Get<KafkaConsumerOptions>()!;
    opts.BootstrapServers = kafkaBootstrap;
    return new TelemetryPersistenceWorker(
        opts,
        sp.GetRequiredService<NpgsqlDataSource>(),
        sp.GetRequiredService<KafkaDlqProducer>(),
        sp.GetRequiredService<ILogger<TelemetryPersistenceWorker>>());
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetryPersistenceWorker>());

builder.Services.AddSingleton(sp =>
{
    var section = builder.Configuration.GetSection("Kafka:Consumers:LatestProjection");
    var opts = section.Get<KafkaConsumerOptions>()!;
    opts.BootstrapServers = kafkaBootstrap;
    return new LatestValueProjectionWorker(
        opts,
        sp.GetRequiredService<IConnectionMultiplexer>(),
        sp.GetRequiredService<KafkaDlqProducer>(),
        sp.GetRequiredService<ILogger<LatestValueProjectionWorker>>());
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<LatestValueProjectionWorker>());

// Controllers + SignalR
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddHttpClient("JaegerClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

// Health Checks
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", failureStatus: HealthStatus.Unhealthy, tags: ["db"])
    .AddCheck<RedisHealthCheck>("redis", failureStatus: HealthStatus.Unhealthy, tags: ["cache"]);

// OTel
builder.Services
    .AddIoTHunterOpenTelemetry(builder.Configuration, "BackendProcessor")
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource("BackendProcessor.Persistence")
        .AddSource("BackendProcessor.LatestProjection"))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddPrometheusExporter());

// Host
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(45);
});

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseOpenTelemetryPrometheusScrapingEndpoint();
app.MapControllers();
app.MapHub<MonitoringHub>("/hubs/monitoring");
app.MapHealthChecks("/health/ready", new()
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            service = "BackendProcessor",
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = e.Value.Duration.TotalMilliseconds
            })
        });
        await context.Response.WriteAsync(json);
    }
});
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

app.Run();
