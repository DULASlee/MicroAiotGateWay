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
    public async Task<IActionResult> PostTelemetry([FromBody] TelemetryRequest request)
    {
        var envelope = TelemetryEnvelopeMapper.ToEnvelope(request, ReliabilityLevel.BestEffort);
        var topic = ReliabilityConfiguration.KafkaTopics[ReliabilityLevel.BestEffort];
        return await ProduceAndRespondAsync(topic, envelope, "http");
    }

    [HttpPost("events/critical")]
    public async Task<IActionResult> PostCriticalEvent([FromBody] TelemetryRequest request)
    {
        var envelope = TelemetryEnvelopeMapper.ToEnvelope(request, ReliabilityLevel.Critical);
        var topic = ReliabilityConfiguration.KafkaTopics[ReliabilityLevel.Critical];
        return await ProduceAndRespondAsync(topic, envelope, "http");
    }

    private async Task<IActionResult> ProduceAndRespondAsync(string topic,
        TelemetryEnvelope envelope, string protocol)
    {
        try
        {
            var result = await _producer.ProduceAsync(topic, envelope);
            _metrics.RecordRequest(protocol, topic, "success");
            _metrics.RecordKafkaLatency(topic, 0);
            return Accepted(new
            {
                eventId = envelope.EventId,
                kafkaPartition = result.Partition.Value,
                kafkaOffset = result.Offset.Value
            });
        }
        catch (Exception)
        {
            _metrics.RecordRejection(protocol, "kafka_unavailable");
            return StatusCode(503, new { status = "unavailable", message = "Kafka not reachable" });
        }
    }
}
