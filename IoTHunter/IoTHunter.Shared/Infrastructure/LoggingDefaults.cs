using Serilog;

namespace IoTHunter.Shared.Infrastructure;

public static class LoggingDefaults
{
    public const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] trace={TraceId} span={SpanId} {Message:lj}{NewLine}{Exception}";

    public static LoggerConfiguration ConfigureBaseLogger(LoggerConfiguration configuration)
    {
        return configuration
            .Enrich.With(new TraceIdEnricher())
            .WriteTo.Console(outputTemplate: ConsoleTemplate);
    }
}
