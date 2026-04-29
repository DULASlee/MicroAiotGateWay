using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DeviceSimulator.Infrastructure.Metrics;
using DeviceSimulator.Infrastructure.Options;
using IoTHunter.Shared.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;

namespace DeviceSimulator;

internal sealed class SimulatorWorker : BackgroundService
{
    private readonly SimulatorOptions _opts;
    private readonly IHttpClientFactory _http;
    private readonly SimulatorMetrics _metrics;
    private readonly ILogger<SimulatorWorker> _logger;

    public SimulatorWorker(
        SimulatorOptions opts,
        IHttpClientFactory http,
        SimulatorMetrics metrics,
        ILogger<SimulatorWorker> logger)
    {
        _opts = opts;
        _http = http;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "Simulator starting: Protocol={Protocol} Devices={Count} Interval={IntervalMs}ms Concurrency={Concurrency}",
            _opts.Protocol, _opts.DeviceCount, _opts.IntervalMs, _opts.Concurrency);

        var channel = Channel.CreateBounded<SendTask>(new BoundedChannelOptions(_opts.Concurrency * 500)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        // Start consumer Tasks
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var consumers = Enumerable.Range(0, _opts.Concurrency)
            .Select(_ => ConsumeLoop(channel.Reader, cts.Token))
            .ToArray();

        // Producer loop
        var endTime = _opts.DurationSeconds > 0
            ? DateTime.UtcNow.AddSeconds(_opts.DurationSeconds)
            : (DateTime?)null;

        var seq = 0L;
        try
        {
            while (!ct.IsCancellationRequested && (endTime is null || DateTime.UtcNow < endTime))
            {
                for (var d = 0; d < _opts.DeviceCount; d++)
                {
                    var deviceId = $"sim-{d:D4}";
                    seq++;
                    var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    var task = new SendTask(
                        deviceId, "simulated_metric",
                        new { value = Random.Shared.NextDouble() * 100, seq },
                        ts, seq);

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
        var client = _http.CreateClient();
        client.BaseAddress = new Uri(_opts.GatewayHttpBase);

        await foreach (var task in reader.ReadAllAsync(ct))
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var payload = new
                {
                    deviceId = task.DeviceId,
                    metricType = task.MetricType,
                    payload = task.Payload,
                    timestamp = task.Timestamp,
                    sequence = task.Sequence
                };
                var json = JsonSerializer.Serialize(payload, SerializerSetup.TightOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var resp = await client.PostAsync("/api/v1/telemetry", content, ct);
                sw.Stop();

                if (resp.IsSuccessStatusCode)
                {
                    _metrics.RecordSent("http");
                    _metrics.RecordLatency("http", sw.ElapsedMilliseconds);
                }
                else
                {
                    _metrics.RecordFailed("http");
                    _logger.LogWarning("HTTP {DeviceId} seq={Seq} status={Status}",
                        task.DeviceId, task.Sequence, (int)resp.StatusCode);
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                _metrics.RecordFailed("http");
                _logger.LogWarning(ex, "HTTP send failed {DeviceId} seq={Seq}", task.DeviceId, task.Sequence);
            }
        }
    }

    private async Task ConsumeMqtt(ChannelReader<SendTask> reader, CancellationToken ct)
    {
        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();

        var clientOptions = new MqttClientOptionsBuilder()
            .WithWebSocketServer(o => o.WithUri(_opts.MqttWebSocketUrl))
            .WithClientId($"sim-{Guid.NewGuid():N}")
            .WithCleanSession()
            .Build();

        await client.ConnectAsync(clientOptions, ct);

        await foreach (var task in reader.ReadAllAsync(ct))
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var topic = $"device/{task.DeviceId}/telemetry";
                var payload = new
                {
                    deviceId = task.DeviceId,
                    metricType = task.MetricType,
                    payload = task.Payload,
                    timestamp = task.Timestamp,
                    sequence = task.Sequence
                };
                var json = JsonSerializer.Serialize(payload, SerializerSetup.TightOptions);

                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(json)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
                    .Build();

                await client.PublishAsync(msg, ct);
                sw.Stop();

                _metrics.RecordSent("mqtt");
                _metrics.RecordLatency("mqtt", sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _metrics.RecordFailed("mqtt");
                _logger.LogWarning(ex, "MQTT send failed {DeviceId} seq={Seq}", task.DeviceId, task.Sequence);
            }
        }

        await client.DisconnectAsync();
    }

    private sealed record SendTask(
        string DeviceId, string MetricType, object? Payload,
        long Timestamp, long Sequence);
}
