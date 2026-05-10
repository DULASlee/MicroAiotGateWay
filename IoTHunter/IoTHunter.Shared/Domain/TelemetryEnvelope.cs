namespace IoTHunter.Shared.Domain;

public sealed record TelemetryEnvelope
{
    public required string EventId { get; init; }
    public required string DeviceId { get; init; }
    public long Sequence { get; init; }
    public required string MetricType { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
    public ReliabilityLevel ReliabilityLevel { get; init; } = ReliabilityLevel.BestEffort;
    public int SchemaVersion { get; init; } = 1;
    public required string PayloadJson { get; init; }

    public static TelemetryEnvelope Create(
        string eventId,
        string deviceId,
        string metricType,
        DateTimeOffset recordedAt,
        string payloadJson,
        ReliabilityLevel reliabilityLevel = ReliabilityLevel.BestEffort,
        long sequence = 0)
    {
        return new TelemetryEnvelope
        {
            EventId = eventId,
            DeviceId = deviceId,
            Sequence = sequence,
            MetricType = metricType,
            RecordedAt = recordedAt,
            ReceivedAt = DateTimeOffset.UtcNow,
            PayloadJson = payloadJson,
            ReliabilityLevel = reliabilityLevel,
            SchemaVersion = 1
        };
    }
}
