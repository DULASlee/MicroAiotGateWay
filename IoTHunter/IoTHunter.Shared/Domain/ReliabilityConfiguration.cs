namespace IoTHunter.Shared.Domain;

public static class ReliabilityConfiguration
{
    public static readonly Dictionary<ReliabilityLevel, string> KafkaTopics = new()
    {
        { ReliabilityLevel.BestEffort, "telemetry.raw" },
        { ReliabilityLevel.AtLeastOnce, "telemetry.raw" },
        { ReliabilityLevel.Critical, "event.critical" }
    };
}
