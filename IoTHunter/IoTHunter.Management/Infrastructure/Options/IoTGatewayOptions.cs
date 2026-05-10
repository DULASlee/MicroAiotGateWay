namespace IoTHunter.Management.Infrastructure.Options;

public sealed class IoTGatewayOptions
{
    public string ConfigUrl { get; set; } = "http://iot-gateway.iothunter.svc.cluster.local/api/v1/config";
}
