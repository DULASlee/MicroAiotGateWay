using Confluent.Kafka;
using System.Text;

namespace BackendProcessor.Infrastructure.Kafka;

internal sealed class KafkaDlqProducer : IDisposable
{
    private readonly IProducer<Null, string> _producer;
    private readonly ILogger<KafkaDlqProducer> _logger;

    public KafkaDlqProducer(string bootstrapServers, ILogger<KafkaDlqProducer> logger)
    {
        _logger = logger;
        _producer = new ProducerBuilder<Null, string>(new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            ClientId = "iot-dlq-producer"
        }).Build();
    }

    public async Task ProduceAsync(string originalMessage, string reason, CancellationToken ct = default)
    {
        var message = new Message<Null, string>
        {
            Value = originalMessage,
            Headers = new Headers { { "failure_reason", Encoding.UTF8.GetBytes(reason) } }
        };
        await _producer.ProduceAsync("telemetry.deadletter", message, ct);
        _logger.LogWarning("DLQ produced: reason={Reason}", reason);
    }

    public void Dispose() => _producer.Dispose();
}
