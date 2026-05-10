using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using IoTHunter.Management.Infrastructure.Options;
using System.Text.Json;

namespace IoTHunter.Management.Controllers;

[ApiController]
[Route("api/v1/gateway")]
[Authorize]
public class GatewayController : ControllerBase
{
    private readonly HttpClient _httpClient;
    private readonly string _gatewayConfigUrl;

    public GatewayController(IHttpClientFactory httpClientFactory, IoTGatewayOptions gatewayOptions)
    {
        _httpClient = httpClientFactory.CreateClient("IoTGateway");
        _gatewayConfigUrl = gatewayOptions.ConfigUrl;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetGatewayStatus()
    {
        try
        {
            var response = await _httpClient.GetStringAsync(_gatewayConfigUrl);
            var gatewayConfig = JsonDocument.Parse(response).RootElement;

            return Ok(new
            {
                gateway = new
                {
                    mqtt = gatewayConfig.GetProperty("mqtt"),
                    kafka = gatewayConfig.GetProperty("kafka")
                },
                defaultDeviceCredentials = new
                {
                    mqttBroker = "mosquitto.iothunter.svc.cluster.local:1883",
                    username = "devicesim",
                    password = "devicesimp"
                }
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException)
        {
            return Ok(new
            {
                gateway = "unreachable",
                message = "IoTGateway is not reachable. Please check if it is running."
            });
        }
    }
}
