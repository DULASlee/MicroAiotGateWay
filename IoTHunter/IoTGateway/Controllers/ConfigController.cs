using IoTHunter.Shared.Domain;
using IoTGateway.Infrastructure.Options;
using Microsoft.AspNetCore.Mvc;

namespace IoTGateway.Controllers;

[ApiController]
[Route("api/v1")]
public sealed class ConfigController : ControllerBase
{
    private readonly MqttOptions _mqtt;
    private readonly KafkaOptions _kafka;

    public ConfigController(MqttOptions mqtt, KafkaOptions kafka)
    {
        _mqtt = mqtt;
        _kafka = kafka;
    }

    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        return Ok(new
        {
            Mqtt = new
            {
                _mqtt.ClientId,
                _mqtt.TcpServer,
                _mqtt.TcpPort
            },
            Kafka = new
            {
                _kafka.BootstrapServers,
                Topics = ReliabilityConfiguration.KafkaTopics.Values.Distinct().ToArray()
            }
        });
    }
}
