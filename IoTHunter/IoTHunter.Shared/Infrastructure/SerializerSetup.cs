using System.Text.Json;
using System.Text.Json.Serialization;

namespace IoTHunter.Shared.Infrastructure;

public static class SerializerSetup
{
    public static readonly JsonSerializerOptions TightOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.Strict,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
