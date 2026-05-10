using StackExchange.Redis;
using System.Text.Json;
using System.Text.RegularExpressions;
using IoTHunter.Shared.Domain;
using IoTHunter.Shared.Infrastructure;
using BackendProcessor.Infrastructure.Options;
using BackendProcessor.Infrastructure.Kafka;
using Confluent.Kafka;

namespace BackendProcessor.Workers;

internal sealed partial class LatestValueProjectionWorker : BackgroundService
{
    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled)]
    private static partial Regex DeviceIdPattern();

    private readonly IConsumer<Null, string> _consumer;
    private readonly IDatabase _database;
    private readonly List<string> _topics;
    private readonly KafkaDlqProducer _dlq;
    private readonly ILogger<LatestValueProjectionWorker> _logger;

    private const string LuaScript = @"
        local currentSeq = redis.call('HGET', KEYS[1], 'sequence')
        if not currentSeq or tonumber(ARGV[1]) >= tonumber(currentSeq) then
            redis.call('HSET', KEYS[1],
                'metric_type', ARGV[2], 'payload_json', ARGV[3],
                'recorded_at', ARGV[4], 'sequence', ARGV[1], 'reliability', ARGV[5])
            redis.call('EXPIRE', KEYS[1], 86400)
            return 1
        end
        return 0";

    public LatestValueProjectionWorker(
        KafkaConsumerOptions kafkaOptions,
        IConnectionMultiplexer redis,
        KafkaDlqProducer dlq,
        ILogger<LatestValueProjectionWorker> logger)
    {
        _database = redis.GetDatabase();
        _topics = kafkaOptions.Topics;
        _dlq = dlq;
        _logger = logger;
        _consumer = new KafkaConsumerFactory().CreateConsumer(kafkaOptions.BootstrapServers, kafkaOptions.GroupId);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _consumer.Subscribe(_topics);

        return Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = _consumer.Consume(TimeSpan.FromMilliseconds(500));
                    if (result is null) continue;

                    var envelope = JsonSerializer.Deserialize<TelemetryEnvelope>(result.Message.Value, SerializerSetup.TightOptions);
                    if (envelope is null)
                    {
                        await _dlq.ProduceAsync(result.Message.Value, "deserialization_failed");
                        continue;
                    }
                    if (!DeviceIdPattern().IsMatch(envelope.DeviceId))
                    {
                        await _dlq.ProduceAsync(result.Message.Value, "invalid_device_id");
                        continue;
                    }

                    var key = new RedisKey($"device:latest:{envelope.DeviceId}");
                    var updated = await _database.ScriptEvaluateAsync(LuaScript,
                        new RedisKey[] { key },
                        new RedisValue[]
                        {
                            envelope.Sequence, envelope.MetricType, envelope.PayloadJson,
                            envelope.RecordedAt.ToString("o"), (int)envelope.ReliabilityLevel
                        });

                    if ((long)updated == 0)
                        _logger.LogDebug("Stale skip key={Key} seq={Seq}", key, envelope.Sequence);

                    _consumer.StoreOffset(result);
                    _consumer.Commit();
                }
                catch (Exception ex) { _logger.LogError(ex, "ProjectionWorker error"); }
            }
        }, stoppingToken);
    }

    public override void Dispose()
    {
        try { _consumer.Close(); } catch (ObjectDisposedException) { }
        try { _consumer.Dispose(); } catch (ObjectDisposedException) { }
        base.Dispose();
    }
}
