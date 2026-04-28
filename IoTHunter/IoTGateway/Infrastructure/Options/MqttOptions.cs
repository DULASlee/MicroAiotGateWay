namespace IoTGateway.Infrastructure.Options;

public sealed class MqttOptions
{
    public string WebSocketUrl { get; set; } = "ws://localhost:8083/mqtt";
    public string ClientId { get; set; } = "iot-gateway-mqtt";
    public int AutoReconnectDelayMs { get; set; } = 5000;
}
