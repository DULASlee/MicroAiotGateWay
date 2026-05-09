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

    public TelemetryController(KafkaProducerService producer, GatewayMetrics metrics)
    {
        _producer = producer;
        _metrics = metrics;
    }

    [HttpPost("telemetry")]
    public async Task<IActionResult> PostTelemetry([FromBody] TelemetryRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            _metrics.RecordRejection("http", "validation_failed");
            return BadRequest(ModelState);
        }

        var envelope = TelemetryEnvelopeMapper.ToEnvelope(request, ReliabilityLevel.BestEffort);
        var topic = ReliabilityConfiguration.KafkaTopics[ReliabilityLevel.BestEffort];
        return await ProduceAndRespondAsync(topic, envelope, "http", ct);
    }

    [HttpPost("events/critical")]
    public async Task<IActionResult> PostCritical([FromBody] TelemetryRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            _metrics.RecordRejection("http", "validation_failed");
            return BadRequest(ModelState);
        }

        var envelope = TelemetryEnvelopeMapper.ToEnvelope(request, ReliabilityLevel.Critical);
        var topic = ReliabilityConfiguration.KafkaTopics[ReliabilityLevel.Critical];
        return await ProduceAndRespondAsync(topic, envelope, "http", ct);
    }

    private async Task<IActionResult> ProduceAndRespondAsync(
        string topic, TelemetryEnvelope envelope, string protocol, CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
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
        catch (Exception)
        {
            stopwatch.Stop();
            _metrics.RecordRequest(protocol, topic, "failure");
            _metrics.RecordRejection(protocol, "kafka_unavailable");

            return StatusCode(503, new
            {
                eventId = envelope.EventId,
                status = "unavailable",
                reason = "Kafka producer unavailable or circuit breaker open"
            });
        }
    }
}
