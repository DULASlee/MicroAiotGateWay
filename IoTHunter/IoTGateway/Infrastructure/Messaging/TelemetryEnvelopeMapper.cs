using System.Text.Json;
using IoTHunter.Shared.Domain;
using IoTGateway.Contracts;

namespace IoTGateway.Infrastructure.Messaging;

internal static class TelemetryEnvelopeMapper
{
    public static TelemetryEnvelope ToEnvelope(TelemetryRequest request, ReliabilityLevel level)
    {
        return TelemetryEnvelope.Create(
            eventId: $"{request.DeviceId}:{request.MetricType}:{request.Sequence}:{request.Timestamp}",
            deviceId: request.DeviceId,
            metricType: request.MetricType,
            recordedAt: DateTimeOffset.FromUnixTimeMilliseconds(request.Timestamp),
            payloadJson: request.Payload.HasValue
                ? JsonSerializer.Serialize(request.Payload.Value, SerializerSetup.TightOptions)
                : "{}",
            reliabilityLevel: level,
            sequence: request.Sequence);
    }
}
