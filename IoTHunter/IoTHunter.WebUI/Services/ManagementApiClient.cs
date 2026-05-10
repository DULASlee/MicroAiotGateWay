using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace IoTHunter.WebUI.Services;

public class ManagementApiClient
{
    private readonly HttpClient _http;
    private readonly AuthTokenHolder _auth;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ManagementApiClient(HttpClient http, AuthTokenHolder auth)
    {
        _http = http;
        _auth = auth;
    }

    public async Task<bool> LoginAsync(string username, string password)
    {
        _auth.BearerToken = null;
        var response = await _http.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        if (!response.IsSuccessStatusCode)
            return false;
        var result = await response.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions);
        if (result?.Token is null)
            return false;
        _auth.BearerToken = result.Token;
        return true;
    }

    private void AttachBearer(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_auth.BearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _auth.BearerToken);
    }

    public async Task<DeviceCredentials?> GetDeviceCredentialsAsync(string deviceId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/devices/{deviceId}/credentials");
        AttachBearer(request);
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DeviceCredentials>(JsonOptions);
    }

    public async Task<GatewayStatus?> GetGatewayStatusAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/gateway/status");
        AttachBearer(request);
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GatewayStatus>(JsonOptions);
    }
}

public record LoginResponse(string Token, string Username);
public record DeviceCredentials(string DeviceId, string MqttBroker, string Username, string Password, string TopicTelemetry, string TopicCritical);
public record GatewayStatus(object Gateway, object DefaultDeviceCredentials);
