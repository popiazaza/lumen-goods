using System.Linq.Expressions;
using Lumen.Domain.Catalog;

namespace Lumen.Api.Catalog;

/// <summary>
/// Catalogue rules as expressions EF Core translates to SQL, so listing filters and the product format use the same
/// definitions. <see cref="Product.IsSellable"/> and <see cref="Product.AvailableQuantity"/> are the in-memory twins;
/// a unit test checks both agree on every combination.
/// </summary>
public static class ProductQueries
{
    /// <summary>
    /// BR-CAT-02: can be bought: active, listed and priced. An unlisted product (dropped by its partner, or moved to an
    /// unmapped category) still opens by id but says "No longer available".
    /// </summary>
    public static readonly Expression<Func<Product, bool>> IsSellable = p => p.IsActive && p.IsListed && p.PriceUsd != null;

    /// <summary>BR-CAT-01: in stock or low stock ("in stock only" filter).</summary>
    public static readonly Expression<Func<Product, bool>> IsInStock = p =>
        p.OnHand != null
            ? p.OnHand - p.Reserved > 0
            : p.Quantity != null
                ? p.Quantity > 0
                : p.StatusWhenQuantityUnknown != AvailabilityStatus.OutOfStock;
}
