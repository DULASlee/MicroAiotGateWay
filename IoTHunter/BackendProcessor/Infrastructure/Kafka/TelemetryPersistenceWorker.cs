using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BackendProcessor.Infrastructure.Options;
using BackendProcessor.Infrastructure.Validation;
using Confluent.Kafka;
using IoTHunter.Shared.Domain;
using IoTHunter.Shared.Infrastructure;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace BackendProcessor.Infrastructure.Kafka;

internal sealed class TelemetryPersistenceWorker : BackgroundService
{
    private static readonly ActivitySource ActivitySource = new("BackendProcessor.Persistence");
    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    private readonly IConsumer<Null, string> _consumer;
    private readonly NpgsqlDataSource _pg;
    private readonly KafkaDlqProducer _dlq;
    private readonly string _groupId;
    private readonly List<string> _topics;
    private readonly ILogger<TelemetryPersistenceWorker> _logger;

    public TelemetryPersistenceWorker(
        KafkaConsumerOptions options,
        NpgsqlDataSource pg,
        KafkaDlqProducer dlq,
        ILogger<TelemetryPersistenceWorker> logger)
    {
        _logger = logger;
        _pg = pg;
        _dlq = dlq;
        _groupId = options.GroupId;
        _topics = options.Topics;
        _consumer = KafkaConsumerFactory.Create(options);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _consumer.Subscribe(_topics);
        _logger.LogInformation(
            "PersistenceWorker started: GroupId={GroupId} Topics={Topics}",
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

                var envelope = DeserializeAndExtractTrace(result);
                if (envelope is not null)
                    batch.Add(result);

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

    private TelemetryEnvelope? DeserializeAndExtractTrace(ConsumeResult<Null, string> result)
    {
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
            "Consume.Persistence", ActivityKind.Consumer, propagationContext.ActivityContext);

        try
        {
            var env = JsonSerializer.Deserialize<TelemetryEnvelope>(result.Message.Value, SerializerSetup.TightOptions);
            if (env is null) return null;

            var dlqReason = EnvelopeValidator.Validate(env);
            if (dlqReason is not null)
            {
                _ = _dlq.ProduceAsync(result.Message.Value, dlqReason, CancellationToken.None);
                return null;
            }

            return env;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Deserialization failed offset={Offset}", result.Offset);
            return null;
        }
    }

    private void ProcessBatch(List<ConsumeResult<Null, string>> batch)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            using var conn = _pg.CreateConnection();
            conn.Open();

            using var cmd = conn.CreateCommand();
            var sql = BuildBatchInsertSql(batch, cmd);
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();

            foreach (var result in batch)
                _consumer.StoreOffset(result);

            _consumer.Commit();

            sw.Stop();
            _logger.LogInformation(
                "Persistence batch: {Count} rows inserted in {DurationMs}ms",
                batch.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex,
                "Persistence batch FAILED: {Count} rows, not committing offsets. Will retry on rebalance.",
                batch.Count);
            throw;
        }
    }

    private static string BuildBatchInsertSql(List<ConsumeResult<Null, string>> batch, NpgsqlCommand cmd)
    {
        var rows = new List<string>(batch.Count);
        for (var i = 0; i < batch.Count; i++)
        {
            var env = JsonSerializer.Deserialize<TelemetryEnvelope>(
                batch[i].Message.Value, SerializerSetup.TightOptions)!;

            var pEvent = $"@e{i}"; var pDev = $"@d{i}"; var pSeq = $"@s{i}";
            var pMetric = $"@m{i}"; var pPayload = $"@p{i}"; var pRecAt = $"@r{i}";
            var pRecvAt = $"@v{i}"; var pSchema = $"@c{i}"; var pRel = $"@l{i}";

            cmd.Parameters.AddWithValue(pEvent, env.EventId);
            cmd.Parameters.AddWithValue(pDev, env.DeviceId);
            cmd.Parameters.AddWithValue(pSeq, env.Sequence);
            cmd.Parameters.AddWithValue(pMetric, env.MetricType);
            cmd.Parameters.AddWithValue(pPayload, NpgsqlTypes.NpgsqlDbType.Jsonb, env.PayloadJson);
            cmd.Parameters.AddWithValue(pRecAt, env.RecordedAt);
            cmd.Parameters.AddWithValue(pRecvAt, env.ReceivedAt);
            cmd.Parameters.AddWithValue(pSchema, env.SchemaVersion);
            cmd.Parameters.AddWithValue(pRel, (short)env.ReliabilityLevel);

            rows.Add(
                $"({pEvent},{pDev},{pSeq},{pMetric},{pPayload},{pRecAt},{pRecvAt},{pSchema},{pRel})");
        }

        return
            $"INSERT INTO telemetry_records (event_id,device_id,sequence,metric_type,payload_json,recorded_at,received_at,schema_version,reliability) VALUES {string.Join(",", rows)} ON CONFLICT (event_id) DO NOTHING;";
    }

    public override void Dispose()
    {
        _consumer.Dispose();
        base.Dispose();
    }
}
