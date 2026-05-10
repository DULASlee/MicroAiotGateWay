using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Confluent.Kafka;
using IoTHunter.Shared.Domain;
using IoTHunter.Shared.Infrastructure;
using BackendProcessor.Infrastructure.Options;
using BackendProcessor.Infrastructure.Kafka;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace BackendProcessor.Workers;

internal sealed partial class TelemetryPersistenceWorker : BackgroundService
{
    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled)]
    private static partial Regex DeviceIdPattern();

    private static readonly ActivitySource ActivitySource = new("BackendProcessor.Persistence");
    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    private readonly IConsumer<Null, string> _consumer;
    private readonly NpgsqlDataSource _pg;
    private readonly List<string> _topics;
    private readonly KafkaDlqProducer _dlq;
    private readonly ILogger<TelemetryPersistenceWorker> _logger;

    public TelemetryPersistenceWorker(
        KafkaConsumerOptions kafkaOptions,
        NpgsqlDataSource pg,
        KafkaDlqProducer dlq,
        ILogger<TelemetryPersistenceWorker> logger)
    {
        _pg = pg;
        _topics = kafkaOptions.Topics;
        _dlq = dlq;
        _logger = logger;

        var factory = new KafkaConsumerFactory();
        _consumer = factory.CreateConsumer(kafkaOptions.BootstrapServers, kafkaOptions.GroupId);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _consumer.Subscribe(_topics);
        _logger.LogInformation("PersistenceWorker subscribed to {Topics}", _topics);

        return Task.Run(() => ConsumeLoop(stoppingToken), stoppingToken);
    }

    private void ConsumeLoop(CancellationToken ct)
    {
        var batch = new List<ConsumeResult<Null, string>>();
        var lastFlush = DateTime.UtcNow;
        var consecutiveFailures = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = _consumer.Consume(TimeSpan.FromMilliseconds(500));
                if (result is not null) batch.Add(result);

                if (batch.Count >= 100 || (batch.Count > 0 && (DateTime.UtcNow - lastFlush).TotalSeconds >= 5))
                {
                    FlushWithRetry(batch, ref consecutiveFailures);
                    batch.Clear();
                    lastFlush = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (batch.Count > 0) FlushWithRetry(batch, ref consecutiveFailures);
            _consumer.Close();
        }
    }

    private void FlushWithRetry(List<ConsumeResult<Null, string>> batch, ref int consecutiveFailures)
    {
        try
        {
            ProcessBatch(batch).GetAwaiter().GetResult();
            consecutiveFailures = 0;
        }
        catch (Exception ex)
        {
            consecutiveFailures++;
            _logger.LogError(ex, "Batch failed ({ConsecutiveFailures} consecutive).", consecutiveFailures);

            if (consecutiveFailures >= 3)
            {
                foreach (var r in batch)
                    _ = _dlq.ProduceAsync(r.Message.Value, "batch_persistent_failure");

                foreach (var r in batch) _consumer.StoreOffset(r);
                try { _consumer.Commit(); }
                catch (KafkaException ke) when (ke.Error.Code == ErrorCode.Local_NoOffset) { }

                _logger.LogWarning("Skipped {Count} messages to DLQ after max batch failures", batch.Count);
                consecutiveFailures = 0;
            }
        }
    }

    private async Task ProcessBatch(List<ConsumeResult<Null, string>> batch)
    {
        using var activity = ActivitySource.StartActivity("BatchWriteToPostgres", ActivityKind.Internal);
        await using var conn = await _pg.OpenConnectionAsync();
        using var tx = await conn.BeginTransactionAsync();

        foreach (var result in batch)
        {
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
            var now = DateTimeOffset.UtcNow;
            if (envelope.RecordedAt > now.AddHours(1) || envelope.RecordedAt < now.AddDays(-7))
            {
                await _dlq.ProduceAsync(result.Message.Value, "timestamp_out_of_range");
                continue;
            }

            var sql = @"
                INSERT INTO telemetry_records
                    (event_id, device_id, sequence, metric_type, payload_json, recorded_at, received_at, schema_version, reliability)
                VALUES
                    (@event_id, @device_id, @sequence, @metric_type, @payload_json::jsonb, @recorded_at, @received_at, @schema_version, @reliability)
                ON CONFLICT (event_id) DO NOTHING;";

            using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("event_id", envelope.EventId);
            cmd.Parameters.AddWithValue("device_id", envelope.DeviceId);
            cmd.Parameters.AddWithValue("sequence", envelope.Sequence);
            cmd.Parameters.AddWithValue("metric_type", envelope.MetricType);
            cmd.Parameters.AddWithValue("payload_json", envelope.PayloadJson);
            cmd.Parameters.AddWithValue("recorded_at", envelope.RecordedAt);
            cmd.Parameters.AddWithValue("received_at", envelope.ReceivedAt);
            cmd.Parameters.AddWithValue("schema_version", envelope.SchemaVersion);
            cmd.Parameters.AddWithValue("reliability", (short)envelope.ReliabilityLevel);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        foreach (var result in batch) _consumer.StoreOffset(result);
        try
        {
            _consumer.Commit();
            _logger.LogInformation("Batch committed, count={Count}", batch.Count);
        }
        catch (KafkaException ex) when (ex.Error.Code == ErrorCode.Local_NoOffset)
        {
            _logger.LogWarning("Commit skipped: no offsets stored for batch of {Count}", batch.Count);
        }
    }

    public override void Dispose()
    {
        try { _consumer.Close(); } catch (ObjectDisposedException) { }
        try { _consumer.Dispose(); } catch (ObjectDisposedException) { }
        base.Dispose();
    }
}
