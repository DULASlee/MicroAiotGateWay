using System.Diagnostics.Metrics;
using System.Collections.Concurrent;

namespace DeviceSimulator.Infrastructure.Metrics;

internal sealed class SimulatorMetrics : IDisposable
{
    private readonly Meter _meter;
    private readonly Counter<long> _sentTotal;
    private readonly Counter<long> _failedTotal;
    private readonly Histogram<double> _ackLatencyMs;
    private long _sentCount;
    private long _failedCount;
    private readonly ConcurrentQueue<double> _latenciesMs = new();
    private readonly DateTime _startedAt = DateTime.UtcNow;

    public SimulatorMetrics()
    {
        _meter = new Meter("DeviceSimulator");
        _sentTotal = _meter.CreateCounter<long>("simulator_sent_total", description: "Total messages sent");
        _failedTotal = _meter.CreateCounter<long>("simulator_failed_total", description: "Total send failures");
        _ackLatencyMs = _meter.CreateHistogram<double>("simulator_ack_latency_ms", "ms", "Ack latency");
    }

    public void RecordSent(string protocol, string reliability, string topic)
    {
        Interlocked.Increment(ref _sentCount);
        _sentTotal.Add(1,
            new KeyValuePair<string, object?>("protocol", protocol),
            new KeyValuePair<string, object?>("reliability", reliability),
            new KeyValuePair<string, object?>("topic", topic));
    }

    public void RecordFailed(string protocol, string reliability, string status)
    {
        Interlocked.Increment(ref _failedCount);
        _failedTotal.Add(1,
            new KeyValuePair<string, object?>("protocol", protocol),
            new KeyValuePair<string, object?>("reliability", reliability),
            new KeyValuePair<string, object?>("status", status));
    }

    public void RecordLatency(string protocol, long ms)
    {
        _latenciesMs.Enqueue(ms);
        _ackLatencyMs.Record(ms,
            new KeyValuePair<string, object?>("protocol", protocol));
    }

    public SimulatorSnapshot GetSnapshot()
    {
        var sent = Interlocked.Read(ref _sentCount);
        var failed = Interlocked.Read(ref _failedCount);
        var total = sent + failed;
        var elapsedSeconds = Math.Max((DateTime.UtcNow - _startedAt).TotalSeconds, 1);

        var latencies = _latenciesMs.ToArray();
        Array.Sort(latencies);

        return new SimulatorSnapshot
        {
            TotalSent = sent,
            TotalFailed = failed,
            SuccessRate = total == 0 ? 100 : (double)sent * 100 / total,
            Qps = total / elapsedSeconds,
            LatencyP99Ms = Percentile(latencies, 0.99),
            LatencyMaxMs = latencies.Length == 0 ? 0 : latencies[^1]
        };
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    public void Dispose() => _meter.Dispose();
}

public sealed class SimulatorSnapshot
{
    public long TotalSent { get; init; }
    public long TotalFailed { get; init; }
    public double SuccessRate { get; init; }
    public double Qps { get; init; }
    public double LatencyP99Ms { get; init; }
    public double LatencyMaxMs { get; init; }
}
