using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IoTGateway.Contracts;

public sealed record TelemetryRequest(
    [property: JsonPropertyName("deviceId")]
    [Required(ErrorMessage = "deviceId is required")]
    [MinLength(1, ErrorMessage = "deviceId must not be empty")]
    string DeviceId,

    [property: JsonPropertyName("metricType")]
    [Required(ErrorMessage = "metricType is required")]
    string MetricType,

    [property: JsonPropertyName("payload")]
    JsonElement? Payload,

    [property: JsonPropertyName("timestamp")]
    [Range(1, long.MaxValue, ErrorMessage = "timestamp must be a positive Unix ms value")]
    long Timestamp,

    [property: JsonPropertyName("sequence")]
    [Range(0, long.MaxValue, ErrorMessage = "sequence must be non-negative")]
    long Sequence
);
