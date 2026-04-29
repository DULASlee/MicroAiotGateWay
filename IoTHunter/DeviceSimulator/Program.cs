using DeviceSimulator;
using DeviceSimulator.Infrastructure.Metrics;
using DeviceSimulator.Infrastructure.Options;
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
        var opts = new SimulatorOptions();
        var cfg = context.Configuration;
        opts.Protocol = cfg["Protocol"] ?? opts.Protocol;
        opts.DeviceCount = int.TryParse(cfg["DeviceCount"], out var dc) ? dc : opts.DeviceCount;
        opts.IntervalMs = int.TryParse(cfg["IntervalMs"], out var iv) ? iv : opts.IntervalMs;
        opts.Concurrency = int.TryParse(cfg["Concurrency"], out var cc) ? cc : opts.Concurrency;
        opts.DurationSeconds = int.TryParse(cfg["DurationSeconds"], out var ds) ? ds : opts.DurationSeconds;
        opts.GatewayHttpBase = cfg["GatewayHttpBase"] ?? opts.GatewayHttpBase;
        opts.MqttWebSocketUrl = cfg["MqttWebSocketUrl"] ?? opts.MqttWebSocketUrl;

        // CLI overrides
        var cmdProtocol = args.FirstOrDefault(a => a.StartsWith("--protocol="))?.Split('=')[1];
        if (cmdProtocol is not null) opts.Protocol = cmdProtocol;

        var cmdDevices = args.FirstOrDefault(a => a.StartsWith("--devices="))?.Split('=')[1];
        if (int.TryParse(cmdDevices, out var devices)) opts.DeviceCount = devices;

        var cmdInterval = args.FirstOrDefault(a => a.StartsWith("--interval="))?.Split('=')[1];
        if (int.TryParse(cmdInterval, out var interval)) opts.IntervalMs = interval;

        var cmdConcurrency = args.FirstOrDefault(a => a.StartsWith("--concurrency="))?.Split('=')[1];
        if (int.TryParse(cmdConcurrency, out var concurrency)) opts.Concurrency = concurrency;

        var cmdDuration = args.FirstOrDefault(a => a.StartsWith("--duration="))?.Split('=')[1];
        if (int.TryParse(cmdDuration, out var duration)) opts.DurationSeconds = duration;

        services.AddSingleton(opts);
        services.AddSingleton<SimulatorMetrics>();
        services.AddHttpClient();

        services.AddIoTHunterOpenTelemetry(context.Configuration, "DeviceSimulator")
            .WithMetrics(metrics => metrics.AddMeter("DeviceSimulator"));

        services.AddHostedService<SimulatorWorker>();
    })
    .Build();

await host.RunAsync();
