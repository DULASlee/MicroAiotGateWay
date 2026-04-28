using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace IoTGateway.Infrastructure.Metrics;

public sealed class GatewayMetrics
{
    private readonly Counter<long> _requestsTotal;
    private readonly Counter<long> _rejectedTotal;
    private readonly Histogram<double> _kafkaAckLatency;
    private readonly Counter<long> _mqttMessagesTotal;

    public GatewayMetrics()
    {
        var meter = new Meter("IoTGateway", "1.0.0");

        _requestsTotal = meter.CreateCounter<long>(
            "gateway_requests_total",
            description: "Total ingestion requests by protocol, topic and status.");

        _rejectedTotal = meter.CreateCounter<long>(
            "gateway_rejected_total",
            description: "Total rejected requests by reason.");

        _kafkaAckLatency = meter.CreateHistogram<double>(
            "gateway_kafka_ack_latency_ms",
            unit: "ms",
            description: "End-to-end Kafka produce latency.");

        _mqttMessagesTotal = meter.CreateCounter<long>(
            "mqtt_messages_total",
            description: "Total MQTT messages received by topic and qos.");
    }

    public void RecordRequest(string protocol, string topic, string status)
    {
        var tags = new TagList
        {
            { "protocol", protocol },
            { "topic", topic },
            { "status", status }
        };
        _requestsTotal.Add(1, tags);
    }

    public void RecordRejection(string protocol, string reason)
    {
        var tags = new TagList
        {
            { "protocol", protocol },
            { "reason", reason }
        };
        _rejectedTotal.Add(1, tags);
    }

    public void RecordKafkaLatency(string topic, double latencyMs)
    {
        var tags = new TagList { { "topic", topic } };
        _kafkaAckLatency.Record(latencyMs, tags);
    }

    public void RecordMqttMessage(string topic, string qos)
    {
        var tags = new TagList
        {
            { "topic", topic },
            { "qos", qos }
        };
        _mqttMessagesTotal.Add(1, tags);
    }
}
