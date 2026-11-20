using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumen.Api.Partners.Harbor;

// Harbor's wire format, written from the published contract only (Lumen shares no code with the stand-in).
// Unknown fields are ignored. Numbers that the contract types loosely (price, on_hand) are read as JsonElement so a
// bad value marks that field invalid instead of discarding the whole item.

internal sealed class HarborItemDto
{
    [JsonPropertyName("item_code")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("dept")]
    public string? Dept { get; set; }

    [JsonPropertyName("unit_price_cents")]
    public JsonElement UnitPriceCents { get; set; }

    [JsonPropertyName("currency_code")]
    public string? CurrencyCode { get; set; }

    [JsonPropertyName("media")]
    public List<HarborMediaDto?>? Media { get; set; }

    [JsonPropertyName("inventory")]
    public HarborInventoryDto? Inventory { get; set; }

    [JsonPropertyName("props")]
    public List<HarborPropDto?>? Props { get; set; }

    [JsonPropertyName("discontinued")]
    public bool? Discontinued { get; set; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }
}

internal sealed class HarborMediaDto
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("href")]
    public string? Href { get; set; }

    [JsonPropertyName("primary")]
    public bool? Primary { get; set; }
}

internal sealed class HarborInventoryDto
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("on_hand")]
    public JsonElement OnHand { get; set; }
}

internal sealed class HarborPropDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

internal static class HarborJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
    };
}
