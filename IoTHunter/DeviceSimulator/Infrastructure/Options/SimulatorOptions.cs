namespace DeviceSimulator.Infrastructure.Options;

public sealed class SimulatorOptions
{
    public string Protocol { get; set; } = "http";
    public int DeviceCount { get; set; } = 10;
    public int IntervalMs { get; set; } = 1000;
    public int Concurrency { get; set; } = 4;
    public int DurationSeconds { get; set; } = 0;
    public double CriticalEventRatio { get; set; } = 0.05;
    public string GatewayHttpBase { get; set; } = "http://localhost:5080";
    public string MqttWebSocketUrl { get; set; } = "ws://localhost:8083/mqtt";

    /// <summary>
    /// 边缘代理 URL。为空时直连网关（默认开发模式）。
    /// HTTP 示例: http://edge-proxy:8080
    /// MQTT 示例: ws://edge-proxy:8083/mqtt
    /// </summary>
    public string ProxyUrl { get; set; } = "";
}
