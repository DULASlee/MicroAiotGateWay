using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BackendProcessor.Infrastructure.Options;
using BackendProcessor.Infrastructure.Validation;
using Confluent.Kafka;
using IoTHunter.Shared.Domain;
using IoTHunter.Shared.Infrastructure;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using StackExchange.Redis;

namespace BackendProcessor.Infrastructure.Kafka;

internal sealed class LatestValueProjectionWorker : BackgroundService
{
    private static readonly ActivitySource ActivitySource = new("BackendProcessor.LatestProjection");
    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    // Lua: compare sequence, reject stale. Returns 1 on update, 0 on skip.
    private const string LuaUpdateScript = """
        local currentSeq = redis.call('HGET', KEYS[1], 'sequence')
        if not currentSeq or tonumber(ARGV[1]) >= tonumber(currentSeq) then
            redis.call('HSET', KEYS[1], 'device_id', ARGV[2], 'metric_type', ARGV[3],
                'payload_json', ARGV[4], 'recorded_at', ARGV[5], 'received_at', ARGV[6],
                'sequence', ARGV[1], 'reliability', ARGV[7], 'event_id', ARGV[8])
            redis.call('EXPIRE', KEYS[1], 86400)
            return 1
        end
        return 0
        """;

    private readonly IConsumer<Null, string> _consumer;
    private readonly ConnectionMultiplexer _redis;
    private readonly KafkaDlqProducer _dlq;
    private readonly string _groupId;
    private readonly List<string> _topics;
    private readonly ILogger<LatestValueProjectionWorker> _logger;

    public LatestValueProjectionWorker(
        KafkaConsumerOptions options,
        ConnectionMultiplexer redis,
        KafkaDlqProducer dlq,
        ILogger<LatestValueProjectionWorker> logger)
    {
        _logger = logger;
        _redis = redis;
        _dlq = dlq;
        _groupId = options.GroupId;
        _topics = options.Topics;
        _consumer = KafkaConsumerFactory.Create(options);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _consumer.Subscribe(_topics);
        _logger.LogInformation(
            "LatestValueWorker started: GroupId={GroupId} Topics={Topics}",
            _groupId, string.Join(",", _topics));

        return Task.Run(() => ConsumeLoop(stoppingToken), stoppingToken);
    }

    private void ConsumeLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = _consumer.Consume(TimeSpan.FromMilliseconds(500));
                if (result is null) continue;

                var propagationContext = Propagator.Extract(
                    default,
                    result.Message.Headers,
                    (headers, key) =>
                    {
                        foreach (var header in headers)
                            if (header.Key == key)
                                return new[] { Encoding.UTF8.GetString(header.GetValueBytes()) };
                        return Array.Empty<string>();
                    });

                Baggage.Current = propagationContext.Baggage;

                using var activity = ActivitySource.StartActivity(
                    "Consume.LatestProjection", ActivityKind.Consumer, propagationContext.ActivityContext);

                try
                {
                    var envelope = JsonSerializer.Deserialize<TelemetryEnvelope>(
                        result.Message.Value, SerializerSetup.TightOptions);
                    if (envelope is null) continue;

                    var dlqReason = EnvelopeValidator.Validate(envelope);
                    if (dlqReason is not null)
                    {
                        _ = _dlq.ProduceAsync(result.Message.Value, dlqReason, CancellationToken.None);
                        _consumer.StoreOffset(result);
                        _consumer.Commit();
                        continue;
                    }

                    ProcessMessage(envelope);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Deserialization failed offset={Offset}", result.Offset);
                }

                _consumer.StoreOffset(result);
                _consumer.Commit();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _consumer.Close();
        }
    }

    private void ProcessMessage(TelemetryEnvelope env)
    {
        var db = _redis.GetDatabase();
        var key = new RedisKey($"device:latest:{env.DeviceId}");

        try
        {
            var result = (int)db.ScriptEvaluate(LuaUpdateScript, new[] { key },
            [
                env.Sequence,
                env.DeviceId,
                env.MetricType,
                env.PayloadJson,
                env.RecordedAt.ToString("O"),
                env.ReceivedAt.ToString("O"),
                (int)env.ReliabilityLevel,
                env.EventId
            ]);

            if (result == 0)
                _logger.LogDebug("Stale skip: {EventId} device={DeviceId} seq={Seq}",
                    env.EventId, env.DeviceId, env.Sequence);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Redis write failed for {EventId}", env.EventId);
        }
    }

    public override void Dispose()
    {
        _consumer.Dispose();
        base.Dispose();
    }
}
