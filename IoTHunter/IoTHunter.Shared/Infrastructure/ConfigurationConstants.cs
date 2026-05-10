namespace IoTHunter.Shared.Infrastructure;

public static class ConfigurationConstants
{
    public const string KafkaBootstrapServers = "kafka:9092";
    public const string MqttWebSocketUrl = "ws://mosquitto:8083/mqtt";
    public const string MqttTcpServer = "mosquitto";
    public const int MqttTcpPort = 1883;

    // Connection string templates — password injected via configuration, never hardcoded
    public const string PostgresConnectionTemplate = "Host=postgres;Database=IoTHunter;Username=iotapp;Password={0}";
    public const string TimescaleDbConnectionTemplate = "Host=timescaledb;Database=IoTHunterTS;Username=iotapp;Password={0}";

    public const string RedisConnection = "redis:6379";
}
