using System.Diagnostics.Metrics;

namespace DeviceSimulator.Infrastructure.Metrics;

internal sealed class SimulatorMetrics : IDisposable
{
    private readonly Meter _meter;
    private readonly Counter<long> _sentTotal;
    private readonly Counter<long> _failedTotal;
    private readonly Histogram<double> _ackLatencyMs;

    public SimulatorMetrics()
    {
        _meter = new Meter("DeviceSimulator");
        _sentTotal = _meter.CreateCounter<long>("simulator_sent_total", description: "Total messages sent");
        _failedTotal = _meter.CreateCounter<long>("simulator_failed_total", description: "Total send failures");
        _ackLatencyMs = _meter.CreateHistogram<double>("simulator_ack_latency_ms", "ms", "Ack latency");
    }

    public void RecordSent(string protocol, string reliability, string topic)
    {
        _sentTotal.Add(1,
            new KeyValuePair<string, object?>("protocol", protocol),
            new KeyValuePair<string, object?>("reliability", reliability),
            new KeyValuePair<string, object?>("topic", topic));
    }

    public void RecordFailed(string protocol, string reliability, string status)
    {
        _failedTotal.Add(1,
            new KeyValuePair<string, object?>("protocol", protocol),
            new KeyValuePair<string, object?>("reliability", reliability),
            new KeyValuePair<string, object?>("status", status));
    }

    public void RecordLatency(string protocol, long ms)
    {
        _ackLatencyMs.Record(ms,
            new KeyValuePair<string, object?>("protocol", protocol));
    }

    public void Dispose() => _meter.Dispose();
}
