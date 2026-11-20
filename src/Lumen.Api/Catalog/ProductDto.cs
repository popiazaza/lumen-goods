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
    DateTime LastUpdatedUtc);

/// <summary><see cref="Currency"/> is always USD; <see cref="Amount"/> always has exactly two decimals.</summary>
public sealed record PriceDto([property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Amount, string Currency);

public sealed record ImageDto(string Url, bool IsPrimary);

/// <param name="Status">in_stock | low_stock | out_of_stock</param>
/// <param name="Quantity">Null when the exact number is not known.</param>
public sealed record AvailabilityDto(string Status, int? Quantity);

public static class ProductMapping
{
    public static ProductDto ToDto(this Product product, AvailabilityPolicy policy, string mediaBaseUrl)
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
            LastUpdatedUtc: product.LastUpdatedUtc.UtcDateTime);
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
