using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumen.Api.Infrastructure.Json;

/// <summary>
/// The API's JSON settings in one place, used by the HTTP pipeline and by tests, so a test of the wire format checks
/// what clients actually receive. Nulls are always written: the product format relies on "quantity": null.
/// </summary>
public static class JsonDefaults
{
    public static void Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    }

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        return options;
    }
}
