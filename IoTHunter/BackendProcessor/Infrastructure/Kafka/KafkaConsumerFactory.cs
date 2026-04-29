using BackendProcessor.Infrastructure.Options;
using Confluent.Kafka;

namespace BackendProcessor.Infrastructure.Kafka;

internal static class KafkaConsumerFactory
{
    public static IConsumer<Null, string> Create(KafkaConsumerOptions options)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            GroupId = options.GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            MaxPollIntervalMs = 300_000,
            SessionTimeoutMs = 30_000,
            HeartbeatIntervalMs = 3_000
        };

        return new ConsumerBuilder<Null, string>(config).Build();
    }
}
