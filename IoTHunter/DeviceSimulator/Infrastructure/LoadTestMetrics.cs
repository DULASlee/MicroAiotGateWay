using System.Collections.Concurrent;

namespace DeviceSimulator.Infrastructure;

internal sealed class LoadTestMetrics
{
    private long _totalSent;
    private long _totalFailed;
    private long _totalBytes;
    private readonly ConcurrentBag<double> _latenciesMs = new();
    private readonly DateTime _start = DateTime.UtcNow;

    public void RecordSent(double latencyMs, int bytes)
    {
        Interlocked.Increment(ref _totalSent);
        Interlocked.Add(ref _totalBytes, bytes);
        _latenciesMs.Add(latencyMs);
    }

    public void RecordFailure()
    {
        Interlocked.Increment(ref _totalFailed);
        Interlocked.Increment(ref _totalSent);
    }

    public SimulatorSnapshot GetSnapshot()
    {
        var elapsed = (DateTime.UtcNow - _start).TotalSeconds;
        var sorted = _latenciesMs.OrderBy(x => x).ToArray();

        return new SimulatorSnapshot
        {
            TotalSent = _totalSent,
            TotalFailed = _totalFailed,
            SuccessRate = _totalSent > 0
                ? (double)(_totalSent - _totalFailed) / _totalSent * 100
                : 0,
            Qps = elapsed > 0 ? _totalSent / elapsed : 0,
            LatencyP99Ms = Percentile(sorted, 0.99),
            LatencyMaxMs = sorted.Length > 0 ? sorted[^1] : 0
        };
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var idx = (int)Math.Ceiling(sorted.Length * p) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }
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
