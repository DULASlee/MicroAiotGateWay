using System.Net.Http.Json;
using System.Text.Json;

namespace IoTHunter.WebUI.Services;

public class SimulatorApiClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SimulatorApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<SimulatorSnapshot?> GetSnapshotAsync()
    {
        return await _http.GetFromJsonAsync<SimulatorSnapshot>("/api/simulator/snapshot", JsonOptions);
    }

    public async Task<SimulatorConfig?> GetConfigAsync()
    {
        return await _http.GetFromJsonAsync<SimulatorConfig>("/api/simulator/config", JsonOptions);
    }
}

public record SimulatorSnapshot(
    long TotalSent,
    long TotalFailed,
    double SuccessRate,
    double Qps,
    double LatencyP99Ms,
    double LatencyMaxMs);

public record SimulatorConfig(
    string Protocol,
    string Target,
    int DeviceCount,
    int IntervalMs,
    int Concurrency,
    int DurationSeconds,
    double CriticalEventRatio);
