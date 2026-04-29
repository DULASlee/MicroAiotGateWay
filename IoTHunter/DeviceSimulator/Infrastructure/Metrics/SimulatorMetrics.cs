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

    public void RecordSent(string protocol) => _sentTotal.Add(1, KeyValuePair.Create<string, object?>("protocol", protocol));
    public void RecordFailed(string protocol) => _failedTotal.Add(1, KeyValuePair.Create<string, object?>("protocol", protocol));
    public void RecordLatency(string protocol, long ms) => _ackLatencyMs.Record(ms, KeyValuePair.Create<string, object?>("protocol", protocol));

    public void Dispose() => _meter.Dispose();
}
