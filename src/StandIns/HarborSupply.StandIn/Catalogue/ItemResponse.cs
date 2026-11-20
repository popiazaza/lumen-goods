using System.Text.Json.Serialization;
using HarborSupply.StandIn.Http;
using HarborSupply.StandIn.Persistence;

namespace HarborSupply.StandIn.Catalogue;

/// <summary>The contract's item shape, field for field and in the sample's order.</summary>
internal sealed record ItemResponse(
    [property: JsonPropertyName("item_code")] string ItemCode,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("dept")] string Dept,
    [property: JsonPropertyName("unit_price_cents")] int? UnitPriceCents,
    [property: JsonPropertyName("currency_code")] string CurrencyCode,
    [property: JsonPropertyName("media")] IReadOnlyList<MediaResponse> Media,
    [property: JsonPropertyName("inventory")] InventoryResponse Inventory,
    [property: JsonPropertyName("props")] IReadOnlyList<PropResponse> Props,
    [property: JsonPropertyName("discontinued")] bool Discontinued,
    [property: JsonPropertyName("updated_at")] string UpdatedAt)
{
    public static ItemResponse From(CatalogItem item) => new(
        item.ItemCode,
        item.Title,
        item.Summary,
        item.Dept,
        item.UnitPriceCents,
        "USD",
        [.. item.ImageViews.Select((view, index) => new MediaResponse("image", MediaPaths.Href(item.ItemCode, view), index == 0))],
        InventoryResponse.For(item.OnHand),
        [.. item.Props.Select(p => new PropResponse(p.Name, p.Value))],
        item.Discontinued,
        HarborJson.FormatTimestamp(item.UpdatedAt));
}

internal sealed record MediaResponse(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("href")] string Href,
    [property: JsonPropertyName("primary")] bool Primary);

internal sealed record PropResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value);

internal sealed record InventoryResponse(
    [property: JsonPropertyName("status")] string Status,
    // Contract: on_hand is present only when the status is LIMITED, so null here means "omit", not "null".
    [property: JsonPropertyName("on_hand"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OnHand)
{
    /// <summary>
    /// The contract names the statuses but not the thresholds. Stand-in rule: 0 is SOLD_OUT, 1 to 10 is LIMITED,
    /// more is AVAILABLE, so Lumen sees LIMITED items on both sides of its own low-stock line of 5.
    /// </summary>
    public const int LimitedMax = 10;

    public static InventoryResponse For(int onHand) => onHand switch
    {
        <= 0 => new("SOLD_OUT", null),
        <= LimitedMax => new("LIMITED", onHand),
        _ => new("AVAILABLE", null),
    };
}

internal sealed record ItemPageResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<ItemResponse> Data,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

internal static class MediaPaths
{
    public const string Extension = ".png";

    /// <summary>An absolute path, so it resolves against the service's origin whatever base path a client uses.</summary>
    public static string Href(string itemCode, string view) => $"/media/{itemCode}/{view}{Extension}";
}
