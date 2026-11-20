using Lumen.Api.Catalog;
using Lumen.Domain.Catalog;

namespace Lumen.Api.Partners.Ports;

/// <summary>
/// Port: one partner's catalogue, already translated into Lumen's vocabulary (the anti-corruption layer). Adapters
/// rely only on the partner's published contract; the sync engine never sees partner formats.
/// Ordering will get its own port (IPartnerOrderGateway) next to this one.
/// </summary>
public interface IPartnerCatalogSource
{
    /// <summary>Partner code, equal to its product source (e.g. "harbor").</summary>
    string Partner { get; }

    PartnerCapabilities Capabilities { get; }

    /// <summary>
    /// Fetches what changed since <see cref="CatalogFetchRequest.Watermark"/>, or everything when
    /// <see cref="CatalogFetchRequest.Full"/>. Throws <see cref="PartnerException"/> when the partner cannot be read.
    /// </summary>
    Task<CatalogFetchResult> FetchCatalogAsync(CatalogFetchRequest request, CancellationToken cancellationToken);
}

/// <summary>Port: stock for partners whose catalogue carries none (Kestrel).</summary>
public interface IPartnerStockSource
{
    string Partner { get; }

    /// <summary>
    /// Stock for the given products. Every id in a batch the partner answered gets a level (unknown when the partner
    /// left it out). Ids in batches that failed are missing from the result and listed in its failures.
    /// Throws <see cref="PartnerException"/> when no batch succeeded.
    /// </summary>
    Task<StockFetchResult> FetchStockAsync(IReadOnlyCollection<string> sourceProductIds, CancellationToken cancellationToken);
}

/// <param name="IncrementalChanges">The partner can list only what changed since a watermark (Harbor's updated_since).</param>
/// <param name="StockInCatalogue">The catalogue carries stock; otherwise an <see cref="IPartnerStockSource"/> supplies it.</param>
public sealed record PartnerCapabilities(bool IncrementalChanges, bool StockInCatalogue);

/// <param name="Full">Fetch everything, ignoring the watermark (Harbor: no updated_since; Kestrel: no If-Modified-Since).</param>
/// <param name="Watermark">The partner-side watermark the previous successful pass returned; opaque to the engine.</param>
public sealed record CatalogFetchRequest(bool Full, string? Watermark);

public enum CatalogFetchKind
{
    /// <summary>Nothing changed since the watermark (Kestrel 304).</summary>
    NotModified,

    /// <summary>Only changed products; products not mentioned are untouched.</summary>
    Changes,

    /// <summary>The whole catalogue; products not in it are no longer offered.</summary>
    Complete,
}

/// <param name="Watermark">What to store once the result is applied (null: no watermark, e.g. no Last-Modified header).</param>
/// <param name="ChangedAtUtc">When the partner produced this catalogue version, if it says (Kestrel's generated).</param>
/// <param name="Problems">Data problems not tied to one stored product (skipped items, duplicates).</param>
public sealed record CatalogFetchResult(
    CatalogFetchKind Kind,
    IReadOnlyList<PartnerProductSnapshot> Products,
    string? Watermark,
    DateTimeOffset? ChangedAtUtc,
    IReadOnlyList<string> Problems)
{
    public static CatalogFetchResult NotModified(string? watermark) => new(CatalogFetchKind.NotModified, [], watermark, null, []);
}

/// <summary>
/// One partner product in Lumen's terms. Prices stay in the partner's currency; the engine converts them with the
/// configured rate so a rate change re-prices products without a partner change.
/// </summary>
/// <param name="Description">Safe HTML (sanitised to the allow-list) or plain text with HTML special characters escaped.</param>
/// <param name="Category">Lumen category, or null when the partner's category is not mapped (the product is not shown).</param>
/// <param name="PartnerCategory">The partner's own category code, for messages.</param>
/// <param name="Price">Unit price in <paramref name="Currency"/>; null when there is no valid price (shown, not sellable).</param>
/// <param name="Images">Absolute partner URLs, exactly one marked primary (or none at all).</param>
/// <param name="Attributes">Keys already normalised to lower camelCase English.</param>
/// <param name="Stock">Null when the catalogue carries no stock (it is then kept as last read).</param>
/// <param name="UpdatedUtc">When the partner says the product last changed, if it does.</param>
/// <param name="Problems">Data problems found while mapping this product (recorded when the product is written).</param>
public sealed record PartnerProductSnapshot(
    string SourceProductId,
    string Name,
    string Description,
    string? Category,
    string PartnerCategory,
    decimal? Price,
    string Currency,
    IReadOnlyList<ProductImage> Images,
    IReadOnlyDictionary<string, string> Attributes,
    bool IsActive,
    StockLevel? Stock,
    DateTimeOffset? UpdatedUtc,
    IReadOnlyList<string> Problems);

/// <summary>What a partner says about stock: an exact quantity, or a status when it gives no number.</summary>
public sealed record StockLevel(int? Quantity, AvailabilityStatus StatusWhenQuantityUnknown)
{
    /// <summary>BR-CAT-01: no evidence of stock is shown as out of stock with an unknown quantity, never in stock.</summary>
    public static readonly StockLevel Unknown = new(null, AvailabilityStatus.OutOfStock);

    public static StockLevel Exactly(int quantity) => new(Math.Max(0, quantity), AvailabilityStatus.OutOfStock);
}

/// <param name="Failures">Messages for batches that could not be read (their products keep their last known stock).</param>
public sealed record StockFetchResult(
    IReadOnlyDictionary<string, StockLevel> Levels,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Failures);
