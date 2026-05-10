using System.Threading.Channels;
using IoTHunter.Shared.Domain;
using DeviceSimulator.Data;
using DeviceSimulator.Infrastructure;

namespace DeviceSimulator.Workers;

internal sealed class SimulatorWorker : BackgroundService
{
    private readonly SimulatorOptions _options;
    private readonly LoadTestMetrics _metrics;
    private readonly ILogger<SimulatorWorker> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpTelemetrySender> _httpSenderLogger;
    private readonly ILogger<MqttTelemetrySender> _mqttSenderLogger;

    public SimulatorWorker(
        SimulatorOptions options,
        LoadTestMetrics metrics,
        ILogger<SimulatorWorker> logger,
        IHttpClientFactory httpClientFactory,
        ILogger<HttpTelemetrySender> httpSenderLogger,
        ILogger<MqttTelemetrySender> mqttSenderLogger)
    {
        _options = options;
        _metrics = metrics;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _httpSenderLogger = httpSenderLogger;
        _mqttSenderLogger = mqttSenderLogger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var httpSender = new HttpTelemetrySender(_httpClientFactory, _httpSenderLogger);
        var mqttSender = new MqttTelemetrySender(_options.MqttBroker, _options.MqttPort,
            _options.MqttUsername, _options.MqttPassword, _mqttSenderLogger);
        if (_options.Protocol == "mqtt") await mqttSender.EnsureConnectedAsync("devicesim", ct);

        var channel = Channel.CreateBounded<TelemetryEnvelope>(new BoundedChannelOptions(_options.Concurrency * 200));
        var generator = new TelemetryGenerator(_options.CriticalEventRatio);
        var endTime = DateTime.UtcNow.AddSeconds(_options.DurationSeconds);

        var consumers = Enumerable.Range(0, _options.Concurrency).Select(async _ =>
        {
            await foreach (var envelope in channel.Reader.ReadAllAsync(ct))
            {
                if (ct.IsCancellationRequested) break;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                SendResult result = _options.Protocol == "http"
                    ? await httpSender.SendAsync(envelope, ct)
                    : await mqttSender.SendAsync(envelope, ct);
                sw.Stop();
                await ProcessResult(envelope, result, sw.Elapsed.TotalMilliseconds, httpSender, mqttSender, ct);
            }
        }).ToList();

        var deviceIds = Enumerable.Range(1, _options.DeviceCount).Select(i => $"dev-{i:D6}").ToList();
        var producer = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && DateTime.UtcNow < endTime)
            {
                foreach (var deviceId in deviceIds)
                {
                    var envelope = generator.Generate(deviceId);
                    await channel.Writer.WriteAsync(envelope, ct);
                }
                await Task.Delay(_options.IntervalMs, ct);
            }
            channel.Writer.Complete();
        }, ct);

        while (!ct.IsCancellationRequested && DateTime.UtcNow < endTime)
        {
            await Task.Delay(5000, ct);
            var snap = _metrics.GetSnapshot();
            _logger.LogInformation(
                "[{Time:HH:mm:ss}] sent={TotalSent} failed={TotalFailed} qps={Qps:F1} p99={P99:F1}ms rate={Rate:F2}%",
                DateTime.Now, snap.TotalSent, snap.TotalFailed, snap.Qps, snap.LatencyP99Ms, snap.SuccessRate);
        }

        await Task.WhenAll(consumers);
        await producer;

        if (_options.Protocol == "mqtt") await mqttSender.DisposeAsync();
        LoadTestReporter.PrintReport(_metrics.GetSnapshot(), _options);
    }

    private async Task ProcessResult(TelemetryEnvelope envelope, SendResult result, double latencyMs,
        HttpTelemetrySender httpSender, MqttTelemetrySender mqttSender, CancellationToken ct)
    {
        if (result == SendResult.Success)
        {
            _metrics.RecordSent(latencyMs, envelope.PayloadJson.Length);
            return;
        }

        for (int i = 1; i <= _options.MaxRetries && result == SendResult.RetryableFailure; i++)
        {
            await Task.Delay(_options.RetryBaseDelayMs * (1 << (i - 1)), ct);
            result = _options.Protocol == "http"
                ? await httpSender.SendAsync(envelope, ct)
                : await mqttSender.SendAsync(envelope, ct);

            if (result == SendResult.Success)
            {
                _metrics.RecordSent(latencyMs + _options.RetryBaseDelayMs * (1 << (i - 1)), envelope.PayloadJson.Length);
                return;
            }
        }
        _metrics.RecordFailure();
    }
}
