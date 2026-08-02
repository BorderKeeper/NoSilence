using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoSilence.Settings;

/// <summary>
/// Shared JSON configuration. <c>settings.json</c> is meant to be hand-editable — people
/// will open it — so comments and trailing commas are tolerated on read and enums are
/// written as names rather than opaque integers.
/// </summary>
internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Compact settings for the one-snapshot-per-line diagnostic recordings.</summary>
    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
