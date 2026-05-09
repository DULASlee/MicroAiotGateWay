namespace IoTGateway.Infrastructure.Options;

public sealed class MqttOptions
{
    public string WebSocketUrl { get; set; } = "ws://mosquitto:8083/mqtt";
    public string TcpServer { get; set; } = "mosquitto";
    public int TcpPort { get; set; } = 1883;
    public string ClientId { get; set; } = "iot-gateway-mqtt";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int AutoReconnectDelayMs { get; set; } = 5000;
    public int RetryBaseDelayMs { get; set; } = 100;
}
