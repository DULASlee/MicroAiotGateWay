using IoTHunter.Shared.Domain;
using IoTGateway.Infrastructure.Options;
using Microsoft.AspNetCore.Mvc;

namespace IoTGateway.Controllers;

[ApiController]
[Route("api/v1")]
public class ConfigController : ControllerBase
{
    private readonly MqttOptions _mqttOptions;
    private readonly KafkaOptions _kafkaOptions;

    public ConfigController(MqttOptions mqttOptions, KafkaOptions kafkaOptions)
    {
        _mqttOptions = mqttOptions;
        _kafkaOptions = kafkaOptions;
    }

    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        return Ok(new
        {
            Mqtt = new
            {
                Server = $"{_mqttOptions.TcpServer}:{_mqttOptions.TcpPort}",
                _mqttOptions.ClientId,
                WebSocketUrl = _mqttOptions.WebSocketUrl,
                TopicTemplates = new[]
                {
                    "device/{deviceId}/telemetry",
                    "device/{deviceId}/event/critical"
                }
            },
            Kafka = new
            {
                _kafkaOptions.BootstrapServers,
                _kafkaOptions.ClientId,
                Topics = ReliabilityConfiguration.KafkaTopics.Values.Distinct().ToArray()
            }
        });
    }
}
