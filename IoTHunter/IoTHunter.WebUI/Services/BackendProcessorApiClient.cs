using System.Net.Http.Json;
using System.Text.Json;

namespace IoTHunter.WebUI.Services;

public class BackendProcessorApiClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public BackendProcessorApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<List<TraceSummary>?> GetTracesAsync(string deviceId, int limit = 10)
    {
        return await _http.GetFromJsonAsync<List<TraceSummary>>($"/api/v2/traces/{deviceId}?limit={limit}", JsonOptions);
    }

    public async Task<DeviceLatest?> GetDeviceLatestAsync(string deviceId)
    {
        return await _http.GetFromJsonAsync<DeviceLatest>($"/api/v2/devices/{deviceId}/latest", JsonOptions);
    }
}

public record TraceSummary(string TraceId, List<TraceSegment> Segments);
public record TraceSegment(string OperationName, double DurationMs);
public record DeviceLatest(string MetricType, string PayloadJson, string RecordedAt, string Sequence, string Reliability);
