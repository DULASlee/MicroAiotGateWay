using System.Diagnostics;
using IoTHunter.Shared.Domain;
using IoTGateway.Contracts;
using IoTGateway.Infrastructure.Messaging;
using IoTGateway.Infrastructure.Metrics;
using Microsoft.AspNetCore.Mvc;

namespace IoTGateway.Controllers;

[ApiController]
[Route("api/v1")]
public sealed class TelemetryController : ControllerBase
{
    private readonly KafkaProducerService _producer;
    private readonly GatewayMetrics _metrics;
    private readonly ILogger<TelemetryController> _logger;

    public TelemetryController(
        KafkaProducerService producer,
        GatewayMetrics metrics,
        ILogger<TelemetryController> logger)
    {
        _producer = producer;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>普通遥测接入：Kafka ack 后返回 202</summary>
    [HttpPost("telemetry")]
    public async Task<IActionResult> PostTelemetry(
        [FromBody] TelemetryRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            _metrics.RecordRejection("http", "validation_failed");
            return BadRequest(ModelState);
        }

        var envelope = TelemetryEnvelopeMapper.ToEnvelope(request, ReliabilityLevel.BestEffort);
        return await ProduceAndRespondAsync("telemetry.raw", envelope, "http", ct);
    }

    /// <summary>关键事件接入：必须 Kafka ack 成功 (ADR-020)</summary>
    [HttpPost("events/critical")]
    public async Task<IActionResult> PostCritical(
        [FromBody] TelemetryRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            _metrics.RecordRejection("http", "validation_failed");
            return BadRequest(ModelState);
        }

        var envelope = TelemetryEnvelopeMapper.ToEnvelope(request, ReliabilityLevel.Critical);
        return await ProduceAndRespondAsync("event.critical", envelope, "http", ct);
    }

    private async Task<IActionResult> ProduceAndRespondAsync(
        string topic, TelemetryEnvelope envelope, string protocol, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await _producer.ProduceAsync(topic, envelope, ct);
            stopwatch.Stop();

            _metrics.RecordKafkaLatency(topic, stopwatch.Elapsed.TotalMilliseconds);
            _metrics.RecordRequest(protocol, topic, "success");

            return Accepted(new
            {
                eventId = envelope.EventId,
                status = "accepted",
                kafkaPartition = result.Partition.Value,
                kafkaOffset = result.Offset.Value,
                latencyMs = stopwatch.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordRequest(protocol, topic, "failure");
            _metrics.RecordRejection(protocol, "kafka_unavailable");

            _logger.LogError(ex,
                "HTTP→Kafka failed {EventId} topic={Topic} latency={LatencyMs}ms",
                envelope.EventId, topic, stopwatch.ElapsedMilliseconds);

            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                eventId = envelope.EventId,
                status = "unavailable",
                reason = "Kafka producer unavailable or circuit breaker open"
            });
        }
    }
}
