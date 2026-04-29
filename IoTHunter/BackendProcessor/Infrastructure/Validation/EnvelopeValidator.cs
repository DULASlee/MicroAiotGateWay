using System.Text.RegularExpressions;
using IoTHunter.Shared.Domain;

namespace BackendProcessor.Infrastructure.Validation;

internal static partial class EnvelopeValidator
{
    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceIdPattern();

    public static string? Validate(TelemetryEnvelope env)
    {
        if (string.IsNullOrEmpty(env.DeviceId) || !DeviceIdPattern().IsMatch(env.DeviceId))
            return "invalid_device_id";

        var now = DateTimeOffset.UtcNow;
        if (env.RecordedAt > now.AddHours(1))
            return "timestamp_out_of_range";

        if (env.RecordedAt < now.AddDays(-7))
            return "timestamp_out_of_range";

        return null; // valid
    }
}
