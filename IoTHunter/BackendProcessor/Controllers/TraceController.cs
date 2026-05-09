using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace BackendProcessor.Controllers;

[ApiController]
[Route("api/v2/traces")]
public sealed class TraceController : ControllerBase
{
    private readonly HttpClient _httpClient;
    private readonly string _jaegerQueryUrl;

    public TraceController(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClient = httpClientFactory.CreateClient("JaegerClient");
        _jaegerQueryUrl = configuration["Jaeger:QueryUrl"] ?? "http://jaeger-query:16686/api/traces";
    }

    [HttpGet("{deviceId}")]
    public async Task<IActionResult> GetTraceSummary(string deviceId, [FromQuery] int limit = 10)
    {
        try
        {
            var url = $"{_jaegerQueryUrl}?service=IoTGateway&tags=deviceId%3A{deviceId}&limit={limit}";
            var response = await _httpClient.GetStringAsync(url);
            var traces = JsonDocument.Parse(response).RootElement.GetProperty("data");
            var summaries = new List<object>();

            foreach (var trace in traces.EnumerateArray())
            {
                var spans = trace.GetProperty("spans");
                var segments = spans.EnumerateArray().Select(s => new
                {
                    operationName = s.GetProperty("operationName").GetString(),
                    durationMs = s.GetProperty("duration").GetInt64() / 1000.0
                });

                summaries.Add(new
                {
                    traceId = trace.GetProperty("traceID").GetString(),
                    segments
                });
            }

            return Ok(summaries);
        }
        catch (HttpRequestException)
        {
            return Ok(Array.Empty<object>());
        }
    }
}
