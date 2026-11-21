using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Lumen.Api.Infrastructure.Json;
using Lumen.Domain.Catalog;
using Lumen.Domain.Money;

namespace Lumen.Api.Catalog;

/// <summary>
/// The storefront product format from the spec: the only product shape the storefront ever sees, whatever the source.
/// </summary>
/// <param name="LastUpdatedUtc">When the product last changed (for a partner product, the partner's own change time when it gives one).</param>
/// <param name="LastCheckedUtc">
/// Lumen's addition to the spec format: when Lumen last confirmed this partner product's price and stock with the partner
/// (<see cref="ProductMapping.LastChecked"/>), which is what the product page's "as of" means. Null for Lumen's own
/// products, whose price and stock are live.
/// </param>
public sealed record ProductDto(
    string Id,
    string Source,
    string SourceProductId,
    string Name,
    string Description,
    string Category,
    PriceDto? Price,
    IReadOnlyList<ImageDto> Images,
    IReadOnlyDictionary<string, string> Attributes,
    AvailabilityDto Availability,
    bool Sellable,
    // DateTime of kind Utc serialises with a "Z" suffix, as in the spec sample; DateTimeOffset would write "+00:00".
    DateTime LastUpdatedUtc,
    DateTime? LastCheckedUtc = null);

/// <summary><see cref="Currency"/> is always USD; <see cref="Amount"/> always has exactly two decimals.</summary>
public sealed record PriceDto([property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Amount, string Currency);

public sealed record ImageDto(string Url, bool IsPrimary);

/// <param name="Status">in_stock | low_stock | out_of_stock</param>
/// <param name="Quantity">Null when the exact number is not known.</param>
public sealed record AvailabilityDto(string Status, int? Quantity);

/// <summary>When a partner's data was last read successfully (its <c>PartnerSyncState</c>).</summary>
/// <param name="CatalogueUtc">The last successful catalogue run; a "nothing changed since" answer counts.</param>
/// <param name="StockUtc">The last complete stock refresh, for a partner with a separate stock feed (Kestrel); else null.</param>
public sealed record PartnerCheck(DateTimeOffset? CatalogueUtc, DateTimeOffset? StockUtc);

public static class ProductMapping
{
    /// <summary>
    /// When Lumen last confirmed a partner product's price and stock: the partner's last successful catalogue run, or its
    /// last complete stock refresh if that is older (both must be current), or a later write to the product itself (a
    /// stock change, or Place order's live check). Not the partner's own edit date, which says nothing about how fresh
    /// Lumen's copy is. Null for Lumen's own products and for a partner never read.
    /// </summary>
    public static DateTimeOffset? LastChecked(Product product, PartnerCheck? partner)
    {
        if (product.Source == ProductSources.Lumen || partner?.CatalogueUtc is not { } catalogue)
        {
            return null;
        }

        var synced = partner.StockUtc is { } stock && stock < catalogue ? stock : catalogue;
        return product.LastUpdatedUtc > synced ? product.LastUpdatedUtc : synced;
    }

    public static ProductDto ToDto(this Product product, AvailabilityPolicy policy, string mediaBaseUrl, DateTimeOffset? lastCheckedUtc = null)
    {
        var availability = policy.Describe(product.AvailableQuantity, product.StatusWhenQuantityUnknown);
        return new ProductDto(
            Id: product.Id.ToString(),
            Source: product.Source,
            SourceProductId: product.SourceProductId,
            Name: product.Name,
            Description: product.Description,
            Category: product.Category,
            Price: product.PriceUsd is { } usd ? new PriceDto(MoneyMath.RoundToCents(usd), "USD") : null,
            Images: [.. product.Images.Select((image, index) => new ImageDto(ImageUrl(mediaBaseUrl, product.Id, index, image.SourceUrl), image.IsPrimary))],
            Attributes: product.Attributes,
            Availability: new AvailabilityDto(ToWire(availability.Status), availability.Quantity),
            Sellable: product.IsSellable,
            LastUpdatedUtc: product.LastUpdatedUtc.UtcDateTime,
            LastCheckedUtc: lastCheckedUtc?.UtcDateTime);
    }

    /// <summary>
    /// The browser-facing image URL. It carries a version derived from where the image comes from, so the media
    /// endpoint can let browsers cache for a day: when a product's image changes, its URL changes too.
    /// </summary>
    public static string ImageUrl(string mediaBaseUrl, Guid productId, int index, string sourceUrl) =>
        $"{mediaBaseUrl}/media/{productId}/{index}?v={ImageVersion(sourceUrl)}";

    /// <summary>First 12 hex characters of SHA-256 of the image's source URL.</summary>
    public static string ImageVersion(string sourceUrl) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceUrl)))[..12];

    public static string ToWire(AvailabilityStatus status) => status switch
    {
        AvailabilityStatus.InStock => "in_stock",
        AvailabilityStatus.LowStock => "low_stock",
        AvailabilityStatus.OutOfStock => "out_of_stock",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
