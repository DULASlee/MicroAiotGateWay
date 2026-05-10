namespace DeviceSimulator.Infrastructure;

public static class LoadProfile
{
    public static SimulatorOptions ApplyScenario(SimulatorOptions opts, string scenario) => scenario switch
    {
        "baseline" => opts with { DeviceCount = 10, IntervalMs = 1000, Concurrency = 5, DurationSeconds = 30 },
        "connection-limit" => opts with { Protocol = "mqtt", DeviceCount = 1000, IntervalMs = 10000, Concurrency = 10, DurationSeconds = 300 },
        "throughput" => opts with { DeviceCount = 200, IntervalMs = 100, Concurrency = 20, DurationSeconds = 300 },
        "stability" => opts with { DeviceCount = 100, IntervalMs = 1000, Concurrency = 30, DurationSeconds = 86400 },
        _ => opts
    };
}
