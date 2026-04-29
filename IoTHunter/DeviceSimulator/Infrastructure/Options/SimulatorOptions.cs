namespace DeviceSimulator.Infrastructure.Options;

public sealed class SimulatorOptions
{
    public string Protocol { get; set; } = "http";
    public int DeviceCount { get; set; } = 10;
    public int IntervalMs { get; set; } = 1000;
    public int Concurrency { get; set; } = 4;
    public int DurationSeconds { get; set; } = 0;
    public string GatewayHttpBase { get; set; } = "http://localhost:5080";
    public string MqttWebSocketUrl { get; set; } = "ws://localhost:8083/mqtt";
}
