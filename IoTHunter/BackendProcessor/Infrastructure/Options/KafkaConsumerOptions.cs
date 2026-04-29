namespace BackendProcessor.Infrastructure.Options;

public sealed class KafkaConsumerOptions
{
    public string BootstrapServers { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public List<string> Topics { get; set; } = new();
}
