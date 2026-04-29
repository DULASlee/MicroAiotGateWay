using BackendProcessor.Infrastructure.Health;
using BackendProcessor.Infrastructure.Kafka;
using BackendProcessor.Infrastructure.Options;
using IoTHunter.Shared.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ---- 1. Serilog ----
builder.Host.UseSerilog((context, services, configuration) =>
{
    LoggingDefaults.ConfigureBaseLogger(configuration);
});

// ---- 2. Data Sources ----
var csPg = builder.Configuration.GetConnectionString("Postgres")!;
builder.Services.AddSingleton(NpgsqlDataSource.Create(csPg));

var csRedis = builder.Configuration.GetConnectionString("Redis")!;
builder.Services.AddSingleton(ConnectionMultiplexer.Connect(csRedis));

var kafkaBootstrap = builder.Configuration["Kafka:BootstrapServers"] ?? "localhost:9092";

// ---- 3. DLQ Producer (shared) ----
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<KafkaDlqProducer>>();
    return new KafkaDlqProducer(kafkaBootstrap, logger);
});

// ---- 4. Kafka Consumer Workers ----

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
        sp.GetRequiredService<ConnectionMultiplexer>(),
        sp.GetRequiredService<KafkaDlqProducer>(),
        sp.GetRequiredService<ILogger<LatestValueProjectionWorker>>());
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<LatestValueProjectionWorker>());

builder.Services.AddSingleton(sp =>
{
    var section = builder.Configuration.GetSection("Kafka:Consumers:TimeseriesProjection");
    var opts = section.Get<KafkaConsumerOptions>()!;
    opts.BootstrapServers = kafkaBootstrap;
    return new TimeseriesProjectionWorker(
        opts, sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<KafkaDlqProducer>(),
        sp.GetRequiredService<ILogger<TimeseriesProjectionWorker>>());
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<TimeseriesProjectionWorker>());

// ---- 4. Controllers ----
builder.Services.AddControllers();

// ---- 5. Health Checks ----
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", failureStatus: HealthStatus.Unhealthy, tags: ["db"])
    .AddCheck<RedisHealthCheck>("redis", failureStatus: HealthStatus.Unhealthy, tags: ["cache"]);

// ---- 5. OTel ----
builder.Services
    .AddIoTHunterOpenTelemetry(builder.Configuration, "BackendProcessor")
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource("BackendProcessor.Persistence")
        .AddSource("BackendProcessor.LatestProjection")
        .AddSource("BackendProcessor.TimeseriesProjection"))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddPrometheusExporter());

// ---- 6. Host Options ----
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(45);
});

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseOpenTelemetryPrometheusScrapingEndpoint();
app.MapControllers();
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

app.Run();
