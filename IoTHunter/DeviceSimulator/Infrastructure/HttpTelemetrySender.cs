using System.Net;
using System.Text;
using System.Text.Json;
using IoTHunter.Shared.Domain;
using IoTHunter.Shared.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DeviceSimulator.Infrastructure;

internal sealed class HttpTelemetrySender
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<HttpTelemetrySender> _logger;

    public HttpTelemetrySender(IHttpClientFactory httpFactory, ILogger<HttpTelemetrySender> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<SendResult> SendAsync(TelemetryEnvelope envelope, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("Gateway");

        var body = JsonSerializer.Serialize(new
        {
            deviceId = envelope.DeviceId,
            metricType = envelope.MetricType,
            payload = envelope.PayloadJson,
            timestamp = envelope.RecordedAt.ToUnixTimeMilliseconds(),
            sequence = envelope.Sequence
        }, SerializerSetup.TightOptions);

        try
        {
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var endpoint = envelope.ReliabilityLevel == ReliabilityLevel.Critical
                ? "/api/v1/events/critical"
                : "/api/v1/telemetry";

            var response = await client.PostAsync(endpoint, content, ct);

            if (response.StatusCode == HttpStatusCode.Accepted)
                return SendResult.Success;
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                return SendResult.RetryableFailure;
            if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                return SendResult.FatalFailure;
            return SendResult.RetryableFailure;
        }
        catch (TaskCanceledException)
        {
            return SendResult.RetryableFailure;
        }
        catch (HttpRequestException)
        {
            return SendResult.RetryableFailure;
        }
    }
}
