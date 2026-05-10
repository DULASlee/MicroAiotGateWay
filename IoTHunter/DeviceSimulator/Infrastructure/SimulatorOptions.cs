namespace DeviceSimulator.Infrastructure;

public sealed record SimulatorOptions
{
    public string Protocol { get; init; } = "http";
    public string GatewayHttpBase { get; init; } = "http://localhost:5080";
    public string MqttWebSocketUrl { get; init; } = "ws://localhost:8083/mqtt";
    public string MqttBroker { get; init; } = "localhost";
    public int MqttPort { get; init; } = 1883;
    public string MqttUsername { get; init; } = "";
    public string MqttPassword { get; init; } = "";
    public int DeviceCount { get; init; } = 10;
    public int IntervalMs { get; init; } = 1000;
    public int Concurrency { get; init; } = 4;
    public int DurationSeconds { get; init; } = 30;
    public double CriticalEventRatio { get; init; } = 0.05;
    public int MaxRetries { get; init; } = 3;
    public int RetryBaseDelayMs { get; init; } = 100;
    public string ProxyUrl { get; init; } = "";
}
