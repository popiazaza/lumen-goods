using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumen.Api.Infrastructure.Json;

/// <summary>
/// The API's JSON settings in one place, used by the HTTP pipeline and by tests, so a test of the wire format checks
/// what clients actually receive. Nulls are always written: the product format relies on "quantity": null.
/// Enums are written as snake_case strings ("awaiting_payment", "in_stock"), the same style as the spec's
/// availability statuses, and read only as those strings (never as numbers), so a reordered enum cannot change the API.
/// </summary>
public static class JsonDefaults
{
    public static void Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
    }

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        return options;
    }
}
