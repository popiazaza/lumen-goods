using System.Globalization;
using System.Text.Json;
using Lumen.Api.Catalog;
using Lumen.Api.Partners.Mapping;
using Lumen.Api.Partners.Ports;
using Lumen.Domain.Catalog;

namespace Lumen.Api.Partners.Harbor;

/// <summary>
/// One Harbor catalogue item → a <see cref="PartnerProductSnapshot"/>. Mapping rules (spec field rules + assumptions):
/// - <c>unit_price_cents</c> null → no price ("price on request", not sellable); not a positive whole number → no price + problem.
/// - <c>inventory.status</c>: AVAILABLE → quantity unknown, in stock; LIMITED → quantity <c>on_hand</c> (status from the
///   number, so LIMITED 8 is in stock, BR-CAT-01), missing on_hand → unknown, low stock; SOLD_OUT → 0; anything else →
///   unknown, out of stock + problem.
/// - <c>discontinued</c> → inactive (hidden, not sellable). <c>dept</c> not in the map → no category (hidden).
/// - <c>media</c>: images only, <c>href</c> resolved against Harbor's base URL, exactly one primary.
/// </summary>
internal static class HarborItemMapper
{
    private const int MaxIdLength = PartnerMappingContext.MaxSourceProductIdLength;

    /// <returns>The snapshot, or null when the item is unusable (no code or no title); <paramref name="problem"/> says why.</returns>
    public static PartnerProductSnapshot? Map(HarborItemDto item, PartnerMappingContext context, out string? problem)
    {
        var code = item.ItemCode?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > MaxIdLength)
        {
            problem = $"Harbor item skipped: item_code '{item.ItemCode}' is missing or longer than {MaxIdLength} characters.";
            return null;
        }

        var title = item.Title?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            problem = $"Harbor item {code} skipped: no title.";
            return null;
        }

        problem = null;
        var problems = new List<string>();
        var dept = item.Dept?.Trim() ?? "";
        var category = context.Categories.Map(dept);

        DateTimeOffset? updated = null;
        if (DateTimeOffset.TryParse(item.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            updated = parsed;
        }
        else
        {
            problems.Add($"Harbor item {code}: updated_at '{item.UpdatedAt}' is not an ISO 8601 timestamp.");
        }

        return new PartnerProductSnapshot(
            SourceProductId: code,
            Name: title,
            // The summary is plain text; passing it through the sanitiser escapes it, so the field is safe HTML either way.
            Description: context.Sanitizer.Sanitize(item.Summary),
            Category: category,
            PartnerCategory: dept,
            Price: ReadPrice(item.UnitPriceCents, code, problems),
            Currency: string.IsNullOrWhiteSpace(item.CurrencyCode) ? CurrencyConverter.Usd : item.CurrencyCode,
            Images: MapImages(item.Media, context.BaseUri),
            Attributes: AttributeKeys.NormalizeAll(
                (item.Props ?? []).Where(p => p?.Name is not null && p.Value is not null)
                    .Select(p => KeyValuePair.Create(p!.Name!, p.Value!)),
                context.AttributeSynonyms),
            IsActive: item.Discontinued != true,
            Stock: MapStock(item.Inventory, code, problems),
            UpdatedUtc: updated,
            Problems: problems);
    }

    private static decimal? ReadPrice(JsonElement cents, string code, List<string> problems)
    {
        if (cents.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        if (cents.ValueKind == JsonValueKind.Number && cents.TryGetDecimal(out var value) && value == decimal.Truncate(value) && value > 0)
        {
            return value / 100m;
        }

        problems.Add($"Harbor item {code}: unit_price_cents {cents.GetRawText()} is not a positive whole number; shown as not sellable.");
        return null;
    }

    private static StockLevel MapStock(HarborInventoryDto? inventory, string code, List<string> problems)
    {
        switch (inventory?.Status?.Trim().ToUpperInvariant())
        {
            case "AVAILABLE":
                return new StockLevel(null, AvailabilityStatus.InStock);
            case "SOLD_OUT":
                return StockLevel.Exactly(0);
            case "LIMITED":
                var onHand = inventory!.OnHand;
                if (onHand.ValueKind == JsonValueKind.Number && onHand.TryGetInt32(out var units))
                {
                    return StockLevel.Exactly(units);
                }

                if (onHand.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                {
                    problems.Add($"Harbor item {code}: on_hand {onHand.GetRawText()} is not a whole number.");
                }

                return new StockLevel(null, AvailabilityStatus.LowStock);
            default:
                problems.Add($"Harbor item {code}: unknown inventory status '{inventory?.Status}'; shown as out of stock.");
                return StockLevel.Unknown;
        }
    }

    private static List<ProductImage> MapImages(List<HarborMediaDto?>? media, Uri baseUri)
    {
        var images = new List<(Uri Url, bool Primary)>();
        foreach (var entry in media ?? [])
        {
            if (entry is null || !string.Equals(entry.Kind?.Trim(), "image", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (PartnerUrls.Resolve(baseUri, entry.Href) is { } url && images.All(i => i.Url != url))
            {
                images.Add((url, entry.Primary == true));
            }
        }

        // Exactly one primary: the first one flagged, else the first image.
        var primary = images.FindIndex(i => i.Primary);
        if (primary < 0)
        {
            primary = 0;
        }

        return [.. images.Select((image, index) => new ProductImage(image.Url.AbsoluteUri, index == primary))];
    }
}
