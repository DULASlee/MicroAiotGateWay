using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IoTHunter.Management.Controllers;

[ApiController]
[Route("api/v1/devices")]
[Authorize]
public class DeviceCredentialsController : ControllerBase
{
    [HttpGet("{deviceId}/credentials")]
    public IActionResult GetDeviceCredentials(string deviceId)
    {
        return Ok(new
        {
            deviceId,
            mqttBroker = "mosquitto.iothunter.svc.cluster.local:1883",
            username = "devicesim",
            password = "devicesimp",
            topicTelemetry = $"device/{deviceId}/telemetry",
            topicCritical = $"device/{deviceId}/event/critical"
        });
    }
}
