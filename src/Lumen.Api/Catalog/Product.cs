using Lumen.Domain.Catalog;

namespace Lumen.Api.Catalog;

/// <summary>
/// One row per product from any source: the read model the storefront queries. Partner rows are written by the
/// partner sync jobs; Lumen's own rows by the admin pages. The storefront never sees partner formats.
/// </summary>
public sealed class Product
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary><see cref="ProductSources"/> value.</summary>
    public required string Source { get; set; }

    /// <summary>The product's id in its source system (Harbor item_code, Kestrel ref, or Lumen's own SKU).</summary>
    public required string SourceProductId { get; set; }

    public required string Name { get; set; }

    /// <summary>Plain text, or HTML already sanitised to the allow-list when the source supplies HTML.</summary>
    public string Description { get; set; } = "";

    /// <summary><see cref="ProductCategories"/> value.</summary>
    public required string Category { get; set; }

    /// <summary>Price in USD rounded to cents; null when the source has no valid price (shown, not sellable).</summary>
    public decimal? PriceUsd { get; set; }

    /// <summary>Price in the source's own currency as received (e.g. EUR for Kestrel), for audit and re-conversion.</summary>
    public decimal? SourcePrice { get; set; }

    public string SourceCurrency { get; set; } = "USD";

    /// <summary>Rate used to convert <see cref="SourcePrice"/> to <see cref="PriceUsd"/>; null for USD sources.</summary>
    public decimal? FxRateToUsd { get; set; }

    /// <summary>Partner products: exact quantity when the partner tells us, otherwise null.</summary>
    public int? Quantity { get; set; }

    /// <summary>Partner products: the status to show when <see cref="Quantity"/> is null.</summary>
    public AvailabilityStatus StatusWhenQuantityUnknown { get; set; } = AvailabilityStatus.OutOfStock;

    /// <summary>Lumen's own products only: units on hand and units reserved by unpaid orders.</summary>
    public int? OnHand { get; set; }

    public int? Reserved { get; set; }

    /// <summary>False for discontinued (Harbor), inactive (Kestrel) or deactivated (Lumen admin) products.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Shown in catalogue listings and search. False when inactive, or when a partner dropped it or moved it to an
    /// unmapped category. Unlisted products still resolve by id, so carts and order history keep working.
    /// </summary>
    public bool IsListed { get; set; } = true;

    public List<ProductImage> Images { get; set; } = [];

    /// <summary>Lower camelCase English keys (e.g. color, size, material).</summary>
    public Dictionary<string, string> Attributes { get; set; } = [];

    /// <summary>When Lumen first saw the product; "newest" sorts by this.</summary>
    public DateTimeOffset FirstSeenUtc { get; set; }

    public DateTimeOffset LastUpdatedUtc { get; set; }

    /// <summary>Hash of the normalised source content, so a sync only writes products that actually changed.</summary>
    public string ContentHash { get; set; } = "";

    /// <summary>PostgreSQL xmin, used as an optimistic concurrency token (admin edits racing with reservations).</summary>
    public uint Version { get; set; }

    public bool IsSellable => IsActive && PriceUsd.HasValue;

    /// <summary>Own products: on hand minus reserved. Partner products: what the partner told us.</summary>
    public int? AvailableQuantity => Source == ProductSources.Lumen
        ? Math.Max(0, (OnHand ?? 0) - (Reserved ?? 0))
        : Quantity;
}

/// <param name="SourceUrl">Where Lumen fetches the image (partner URL or own asset path); never sent to the browser.</param>
public sealed record ProductImage(string SourceUrl, bool IsPrimary);
