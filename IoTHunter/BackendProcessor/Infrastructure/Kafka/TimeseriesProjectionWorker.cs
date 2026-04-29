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

namespace BackendProcessor.Infrastructure.Kafka;

internal sealed class TimeseriesProjectionWorker : BackgroundService
{
    private static readonly ActivitySource ActivitySource = new("BackendProcessor.TimeseriesProjection");
    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    private readonly IConsumer<Null, string> _consumer;
    private readonly KafkaDlqProducer _dlq;
    private readonly string _groupId;
    private readonly List<string> _topics;
    private readonly bool _enabled;
    private readonly ILogger<TimeseriesProjectionWorker> _logger;

    public TimeseriesProjectionWorker(
        KafkaConsumerOptions options,
        IConfiguration configuration,
        KafkaDlqProducer dlq,
        ILogger<TimeseriesProjectionWorker> logger)
    {
        _logger = logger;
        _dlq = dlq;
        _groupId = options.GroupId;
        _topics = options.Topics;
        _enabled = configuration.GetValue<bool>("TimeseriesProjection:Enabled");
        _consumer = KafkaConsumerFactory.Create(options);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("TimeseriesProjectionWorker disabled via config");
            return Task.CompletedTask;
        }

        _consumer.Subscribe(_topics);
        _logger.LogInformation(
            "TimeseriesWorker started: GroupId={GroupId} Topics={Topics}",
            _groupId, string.Join(",", _topics));

        return Task.Run(() => ConsumeLoop(stoppingToken), stoppingToken);
    }

    private void ConsumeLoop(CancellationToken ct)
    {
        var batch = new List<ConsumeResult<Null, string>>();
        var lastFlush = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = _consumer.Consume(TimeSpan.FromMilliseconds(500));
                if (result is null)
                {
                    if (batch.Count > 0 && (DateTime.UtcNow - lastFlush).TotalSeconds >= 5)
                    {
                        ProcessBatch(batch);
                        batch.Clear();
                        lastFlush = DateTime.UtcNow;
                    }
                    continue;
                }

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
                    "Consume.TimeseriesProjection", ActivityKind.Consumer, propagationContext.ActivityContext);

                try
                {
                    var envelope = JsonSerializer.Deserialize<TelemetryEnvelope>(result.Message.Value, SerializerSetup.TightOptions);
                    if (envelope is null) continue;

                    var dlqReason = EnvelopeValidator.Validate(envelope);
                    if (dlqReason is not null)
                    {
                        _ = _dlq.ProduceAsync(result.Message.Value, dlqReason, CancellationToken.None);
                        _consumer.StoreOffset(result);
                        _consumer.Commit();
                        continue;
                    }

                    batch.Add(result);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Deserialization failed offset={Offset}", result.Offset);
                }

                if (batch.Count >= 100 || (batch.Count > 0 && (DateTime.UtcNow - lastFlush).TotalSeconds >= 5))
                {
                    ProcessBatch(batch);
                    batch.Clear();
                    lastFlush = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (batch.Count > 0) ProcessBatch(batch);
            _consumer.Close();
        }
    }

    private void ProcessBatch(List<ConsumeResult<Null, string>> batch)
    {
        _logger.LogInformation("Timeseries batch: {Count} messages", batch.Count);

        foreach (var result in batch)
            _consumer.StoreOffset(result);

        _consumer.Commit();
    }

    public override void Dispose()
    {
        _consumer.Dispose();
        base.Dispose();
    }
}
