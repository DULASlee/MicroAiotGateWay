using System.Text.Json;
using IoTHunter.Shared.Domain;
using IoTHunter.Shared.Infrastructure;

namespace DeviceSimulator.Infrastructure;

internal sealed class TelemetryGenerator
{
    private long _globalSeq;

    public TelemetryEnvelope Generate(string deviceId, ReliabilityLevel reliability, long? sequence = null)
    {
        var seq = sequence ?? Interlocked.Increment(ref _globalSeq);
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeMilliseconds();
        var metricType = reliability == ReliabilityLevel.Critical
            ? "critical_event"
            : "simulated_metric";

        var payloadJson = JsonSerializer.Serialize(
            new { value = Random.Shared.NextDouble() * 100, seq },
            SerializerSetup.TightOptions);

        return TelemetryEnvelope.Create(
            eventId: $"{deviceId}:{metricType}:{seq}:{ts}",
            deviceId: deviceId,
            metricType: metricType,
            recordedAt: now,
            payloadJson: payloadJson,
            reliabilityLevel: reliability,
            sequence: seq);
    }
}
