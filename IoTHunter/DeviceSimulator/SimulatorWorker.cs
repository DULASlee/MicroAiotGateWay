using System.Diagnostics;
using System.Threading.Channels;
using DeviceSimulator.Infrastructure;
using DeviceSimulator.Infrastructure.Metrics;
using DeviceSimulator.Infrastructure.Options;
using IoTHunter.Shared.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeviceSimulator;

internal sealed class SimulatorWorker : BackgroundService
{
    private readonly SimulatorOptions _opts;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SimulatorMetrics _metrics;
    private readonly ILogger<SimulatorWorker> _logger;

    public SimulatorWorker(
        IOptions<SimulatorOptions> opts,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory,
        SimulatorMetrics metrics,
        ILogger<SimulatorWorker> logger)
    {
        _opts = opts.Value;
        _httpFactory = httpFactory;
        _loggerFactory = loggerFactory;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "Simulator starting: Protocol={Protocol} Devices={Count} Interval={IntervalMs}ms Concurrency={Concurrency} CriticalRatio={CriticalRatio}",
            _opts.Protocol, _opts.DeviceCount, _opts.IntervalMs,
            _opts.Concurrency, _opts.CriticalEventRatio);

        var channel = Channel.CreateBounded<SendTask>(new BoundedChannelOptions(_opts.Concurrency * 500)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var consumers = Enumerable.Range(0, _opts.Concurrency)
            .Select(_ => ConsumeLoop(channel.Reader, cts.Token))
            .ToArray();

        // Producer loop
        var endTime = _opts.DurationSeconds > 0
            ? DateTime.UtcNow.AddSeconds(_opts.DurationSeconds)
            : (DateTime?)null;

        var rng = new Random();
        var seq = 0L;
        try
        {
            while (!ct.IsCancellationRequested && (endTime is null || DateTime.UtcNow < endTime))
            {
                for (var d = 0; d < _opts.DeviceCount; d++)
                {
                    var deviceId = $"dev-{d:D3}";
                    seq++;

                    // 4.7: 按 CriticalEventRatio 决定可靠性级别
                    var isCritical = rng.NextDouble() < _opts.CriticalEventRatio;
                    var reliability = isCritical ? ReliabilityLevel.Critical : ReliabilityLevel.BestEffort;

                    var task = new SendTask(
                        deviceId,
                        isCritical ? "critical_event" : "simulated_metric",
                        new { value = rng.NextDouble() * 100, seq },
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        seq,
                        reliability);

                    await channel.Writer.WriteAsync(task, ct);
                }

                await Task.Delay(_opts.IntervalMs, ct);
            }

            _logger.LogInformation("Producer done: {Seq} messages generated", seq);
        }
        catch (OperationCanceledException) { }
        finally
        {
            channel.Writer.Complete();
            _logger.LogInformation("Channel writer completed, waiting for consumers to drain...");
        }

        await Task.WhenAll(consumers);
        _logger.LogInformation("Simulator stopped");
    }

    private async Task ConsumeLoop(ChannelReader<SendTask> reader, CancellationToken ct)
    {
        if (_opts.Protocol.Equals("mqtt", StringComparison.OrdinalIgnoreCase))
            await ConsumeMqtt(reader, ct);
        else
            await ConsumeHttp(reader, ct);
    }

    private async Task ConsumeHttp(ChannelReader<SendTask> reader, CancellationToken ct)
    {
        var generator = new TelemetryGenerator();
        var sender = new HttpTelemetrySender(
            _httpFactory, _loggerFactory.CreateLogger<HttpTelemetrySender>());

        await foreach (var task in reader.ReadAllAsync(ct))
        {
            var sw = Stopwatch.StartNew();
            var envelope = generator.Generate(task.DeviceId, task.Reliability, task.Sequence);
            var result = await SendWithRetryAsync(
                envelope, (e, c) => sender.SendAsync(e, c), ct);
            sw.Stop();

            var rel = task.Reliability == ReliabilityLevel.Critical ? "critical" : "best_effort";
            var topic = task.Reliability == ReliabilityLevel.Critical ? "event.critical" : "telemetry.raw";

            if (result == SendResult.Success)
            {
                _metrics.RecordSent("http", rel, topic);
                _metrics.RecordLatency("http", sw.ElapsedMilliseconds);
            }
            else
            {
                var status = result == SendResult.FatalFailure ? "fatal" : "retry_exhausted";
                _metrics.RecordFailed("http", rel, status);
            }
        }
    }

    private async Task ConsumeMqtt(ChannelReader<SendTask> reader, CancellationToken ct)
    {
        // 边缘代理模式：ProxyUrl 非空时走代理，否则直连网关
        var proxyUrl = _opts.ProxyUrl;
        var effectiveUrl = string.IsNullOrWhiteSpace(proxyUrl)
            ? _opts.MqttWebSocketUrl
            : proxyUrl;
        var uri = new Uri(effectiveUrl);
        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 1883;

        var generator = new TelemetryGenerator();
        await using var sender = new MqttTelemetrySender(
            _loggerFactory.CreateLogger<MqttTelemetrySender>());
        await sender.EnsureConnectedAsync(host, port, "simulator", ct);

        await foreach (var task in reader.ReadAllAsync(ct))
        {
            if (!sender.IsConnected)
            {
                var rel = task.Reliability == ReliabilityLevel.Critical ? "critical" : "best_effort";
                _metrics.RecordFailed("mqtt", rel, "fatal");
                continue;
            }

            var sw = Stopwatch.StartNew();
            var envelope = generator.Generate(task.DeviceId, task.Reliability, task.Sequence);
            var result = await SendWithRetryAsync(
                envelope, (e, c) => sender.SendAsync(e, c), ct);
            sw.Stop();

            var rel2 = task.Reliability == ReliabilityLevel.Critical ? "critical" : "best_effort";
            var topic = task.Reliability == ReliabilityLevel.Critical ? "event.critical" : "telemetry.raw";

            if (result == SendResult.Success)
            {
                _metrics.RecordSent("mqtt", rel2, topic);
                _metrics.RecordLatency("mqtt", sw.ElapsedMilliseconds);
            }
            else
            {
                var status = result == SendResult.FatalFailure ? "fatal" : "retry_exhausted";
                _metrics.RecordFailed("mqtt", rel2, status);
            }
        }
    }

    /// <summary>
    /// 指数退避有限重试：3 次重试（100ms → 200ms → 400ms），FatalFailure 不重试。
    /// </summary>
    private async Task<SendResult> SendWithRetryAsync(
        TelemetryEnvelope envelope,
        Func<TelemetryEnvelope, CancellationToken, Task<SendResult>> send,
        CancellationToken ct)
    {
        const int maxRetries = 3;
        var delays = new[] { 100, 200, 400 };

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(delays[attempt - 1], ct);

            var result = await send(envelope, ct);

            if (result != SendResult.RetryableFailure)
                return result;

            _logger.LogDebug("Retry {Attempt}/{Max} for {EventId}",
                attempt, maxRetries, envelope.EventId);
        }

        _logger.LogWarning("Retries exhausted for {EventId}", envelope.EventId);
        return SendResult.RetryableFailure;
    }

    private sealed record SendTask(
        string DeviceId, string MetricType, object? Payload,
        long Timestamp, long Sequence, ReliabilityLevel Reliability);
}
