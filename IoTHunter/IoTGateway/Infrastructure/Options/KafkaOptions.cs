namespace IoTGateway.Infrastructure.Options;

public sealed class KafkaOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";
    public int MessageTimeoutMs { get; set; } = 5000;
    public string ClientId { get; set; } = "iot-gateway";
    public int MaxInFlight { get; set; } = 5;
}
