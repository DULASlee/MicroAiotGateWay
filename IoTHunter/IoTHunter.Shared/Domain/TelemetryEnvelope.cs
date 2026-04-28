namespace IoTHunter.Shared.Domain;

public sealed record TelemetryEnvelope
{
    public required string EventId { get; init; }

    public required string DeviceId { get; init; }

    public long Sequence { get; init; }

    public required string MetricType { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }

    public required string PayloadJson { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }

    public required ReliabilityLevel ReliabilityLevel { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public static TelemetryEnvelope Create(
        string eventId,
        string deviceId,
        string metricType,
        DateTimeOffset recordedAt,
        string payloadJson,
        ReliabilityLevel reliabilityLevel,
        long sequence = 0)
    {
        return new TelemetryEnvelope
        {
            EventId = eventId,
            DeviceId = deviceId,
            MetricType = metricType,
            RecordedAt = recordedAt,
            PayloadJson = payloadJson,
            ReliabilityLevel = reliabilityLevel,
            Sequence = sequence,
            ReceivedAt = DateTimeOffset.UtcNow,
        };
    }
}
