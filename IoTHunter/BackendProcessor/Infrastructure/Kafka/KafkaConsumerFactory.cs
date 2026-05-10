using Confluent.Kafka;

namespace BackendProcessor.Infrastructure.Kafka;

internal sealed class KafkaConsumerFactory
{
    public IConsumer<Null, string> CreateConsumer(string bootstrapServers, string groupId)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = groupId,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AutoOffsetReset = AutoOffsetReset.Earliest
        };
        return new ConsumerBuilder<Null, string>(config).Build();
    }
}
