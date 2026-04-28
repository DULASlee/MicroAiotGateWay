using IoTHunter.Shared.Infrastructure;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) =>
{
    LoggingDefaults.ConfigureBaseLogger(configuration);
});

builder.Services.AddControllers();

builder.Services
    .AddIoTHunterOpenTelemetry(builder.Configuration, "BackendProcessor")
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddPrometheusExporter());

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseOpenTelemetryPrometheusScrapingEndpoint();
app.MapControllers();
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready", service = "BackendProcessor" }));

app.Run();
