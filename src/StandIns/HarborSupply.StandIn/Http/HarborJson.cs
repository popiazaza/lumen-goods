using System.Globalization;
using System.Text.Json;

namespace HarborSupply.StandIn.Http;

internal static class HarborJson
{
    // Contract field names are spelled out with [JsonPropertyName] on each type, so there is no naming policy
    // and no case-insensitive matching: what the code says is exactly what goes over the wire.
    public static readonly JsonSerializerOptions Options = new();

    /// <summary>The contract's timestamp format: UTC, whole seconds, "Z" suffix (2026-10-02T09:15:00Z).</summary>
    public static string FormatTimestamp(DateTime utc) =>
        utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
