using IoTHunter.Shared.Domain;
using System.Text.Json;

namespace DeviceSimulator.Data;

internal sealed class TelemetryGenerator
{
    private readonly Dictionary<string, long> _sequences = new();
    private readonly string[] _metrics = { "heart_rate", "spo2", "steps", "temperature", "sleep_score" };
    private readonly Random _random = new();
    private readonly double _criticalRatio;

    public TelemetryGenerator(double criticalRatio = 0.05) => _criticalRatio = criticalRatio;

    public TelemetryEnvelope Generate(string deviceId)
    {
        if (!_sequences.ContainsKey(deviceId)) _sequences[deviceId] = 0;
        var seq = ++_sequences[deviceId];
        var metric = _metrics[_random.Next(_metrics.Length)];
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(GeneratePayload(metric));
        var reliability = _random.NextDouble() < _criticalRatio ? ReliabilityLevel.Critical : ReliabilityLevel.BestEffort;
        return TelemetryEnvelope.Create(
            eventId: $"{deviceId}:{metric}:{seq}:{now.ToUnixTimeMilliseconds()}",
            deviceId: deviceId,
            metricType: metric,
            recordedAt: now,
            payloadJson: payload,
            reliabilityLevel: reliability,
            sequence: seq);
    }

    private object GeneratePayload(string metric) => metric switch
    {
        "heart_rate" => new { bpm = _random.Next(60, 120) },
        "spo2" => new { percentage = Math.Round(95.0 + _random.NextDouble() * 5.0, 1) },
        "steps" => new { count = _random.Next(0, 500) },
        "temperature" => new { celsius = Math.Round(36.0 + _random.NextDouble() * 1.5, 1) },
        "sleep_score" => new { score = _random.Next(0, 100) },
        _ => new { value = _random.Next(0, 100) }
    };
}
