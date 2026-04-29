using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using IoTHunter.Shared.Infrastructure;

namespace BackendProcessor.Infrastructure.Kafka;

internal sealed class KafkaDlqProducer : IDisposable
{
    private const string DlqTopic = "telemetry.deadletter";

    private readonly IProducer<Null, string> _producer;
    private readonly ILogger<KafkaDlqProducer> _logger;

    public KafkaDlqProducer(string bootstrapServers, ILogger<KafkaDlqProducer> logger)
    {
        _logger = logger;
        var config = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            ClientId = "iot-dlq-producer",
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = 10_000
        };
        _producer = new ProducerBuilder<Null, string>(config).Build();
    }

    public async Task ProduceAsync(string originalPayload, string reason, CancellationToken ct)
    {
        try
        {
            var dlqEnvelope = new
            {
                original_payload = originalPayload,
                failure_reason = reason,
                dlq_received_at = DateTimeOffset.UtcNow
            };

            var json = JsonSerializer.Serialize(dlqEnvelope, SerializerSetup.TightOptions);
            var message = new Message<Null, string>
            {
                Value = json,
                Headers = [new Header("failure_reason", Encoding.UTF8.GetBytes(reason))]
            };

            await _producer.ProduceAsync(DlqTopic, message, ct);
            _logger.LogWarning("DLQ routed: reason={Reason}", reason);
        }
        catch (ProduceException<Null, string> ex)
        {
            _logger.LogError(ex, "DLQ produce failed: reason={Reason}", reason);
        }
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}
