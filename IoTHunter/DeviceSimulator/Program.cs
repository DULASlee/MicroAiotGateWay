using DeviceSimulator;
using IoTHunter.Shared.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

var host = Host.CreateDefaultBuilder(args)
    .UseSerilog((context, services, configuration) =>
    {
        LoggingDefaults.ConfigureBaseLogger(configuration);
    })
    .ConfigureServices((context, services) =>
    {
        services.AddHttpClient();
        services.AddIoTHunterOpenTelemetry(context.Configuration, "DeviceSimulator");
        services.AddHostedService<PlaceholderWorker>();
    })
    .Build();

await host.RunAsync();
