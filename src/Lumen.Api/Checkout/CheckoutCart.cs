using System.Globalization;
using System.Security.Claims;
using Lumen.Api.Carts;
using Lumen.Api.Catalog;
using Lumen.Api.Infrastructure.Data;
using Lumen.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Checkout;

/// <summary>A line of the customer's saved cart with its product as it is now.</summary>
public sealed record CheckoutCartLine(CartLine Line, Product Product)
{
    public bool IsOwn => Product.Source == ProductSources.Lumen;
}

/// <summary>
/// Reads the signed-in customer's saved cart for the preview and Place order, and works out which lines block checkout
/// (BR-CART-03) from Lumen's own data. Partner lines are checked live afterwards by Place order (BR-CHK-02).
/// </summary>
internal sealed class CheckoutCart(LumenDbContext db, IOptions<CatalogOptions> catalog)
{
    /// <summary>Lines in a stable order: by seller (Lumen, Harbor, Kestrel), then as added. This becomes the line numbering.</summary>
    public async Task<IReadOnlyList<CheckoutCartLine>> LoadAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var lines = await db.CartLines.AsNoTracking()
            .Where(l => db.Carts.Any(c => c.Id == l.CartId && c.CustomerId == customerId))
            .Include(l => l.Product)
            .ToListAsync(cancellationToken);

        return [.. lines
            .Select(l => new CheckoutCartLine(l, l.Product!))
            .OrderBy(l => SellerOrder(l.Product.Source))
            .ThenBy(l => l.Line.AddedUtc)
            .ThenBy(l => l.Line.Id)];
    }

    /// <summary>
    /// BR-CART-03: why a line cannot be ordered as it is, from Lumen's own data, or null. Not sellable → "No longer
    /// available"; none in stock (or no evidence of stock) → "Out of stock"; more than the known quantity → "Only n left".
    /// </summary>
    public CheckoutLineChangeDto? Blocker(CheckoutCartLine line)
    {
        var product = line.Product;
        if (!product.IsSellable)
        {
            return Change(line, CheckoutChangeKind.Unavailable, message: "No longer available");
        }

        var available = product.AvailableQuantity;
        if (available is 0 || (available is null && product.StatusWhenQuantityUnknown == AvailabilityStatus.OutOfStock))
        {
            return Change(line, CheckoutChangeKind.Unavailable, available: 0, message: "Out of stock");
        }

        return available is { } known && known < line.Line.Quantity
            ? Change(line, CheckoutChangeKind.QuantityLimited, available: known, message: $"Only {known} left")
            : null;
    }

    public CheckoutLineChangeDto Change(
        CheckoutCartLine line, CheckoutChangeKind kind, decimal? from = null, decimal? to = null, int? available = null, string? message = null) =>
        new(line.Line.Id, line.Product.Id, line.Product.Name, SellerName(line.Product.Source), kind, from, to, null, null, available,
            message ?? DefaultMessage(kind, from, to, available));

    /// <summary>A line whose quantity differs from the review, or that is on one side only (cart_changed).</summary>
    /// <param name="cartLineId">Null for a reviewed product that is no longer in the cart.</param>
    public CheckoutLineChangeDto QuantityChange(Guid? cartLineId, Product product, int reviewed, int inCart)
    {
        var (kind, message) = (reviewed, inCart) switch
        {
            (0, _) => (CheckoutChangeKind.CartChanged, "Added to your cart since you reviewed it"),
            (_, 0) => (CheckoutChangeKind.CartChanged, "Removed from your cart since you reviewed it"),
            _ => (CheckoutChangeKind.QuantityChanged, $"Quantity changed from {reviewed} to {inCart}"),
        };
        return new CheckoutLineChangeDto(cartLineId, product.Id, product.Name, SellerName(product.Source), kind, null, null, reviewed, inCart, null, message);
    }

    public string SellerName(string source) =>
        catalog.Value.Sellers.FirstOrDefault(s => s.Source == source)?.Name ?? source;

    public static string Money(decimal amount) => "$" + amount.ToString("#,##0.00", CultureInfo.InvariantCulture);

    /// <summary>The index of the product's primary image (the one the order and preview show), or null without images.</summary>
    public static int? PrimaryImageIndex(Product product)
    {
        var index = product.Images.FindIndex(i => i.IsPrimary);
        return index >= 0 ? index : product.Images.Count > 0 ? 0 : null;
    }

    private static string DefaultMessage(CheckoutChangeKind kind, decimal? from, decimal? to, int? available) => kind switch
    {
        CheckoutChangeKind.PriceChanged => $"Price changed from {Money(from ?? 0)} to {Money(to ?? 0)}",
        CheckoutChangeKind.QuantityLimited => $"Only {available ?? 0} available",
        CheckoutChangeKind.TemporarilyUnavailable => "Temporarily unavailable",
        _ => available == 0 ? "Out of stock" : "No longer available",
    };

    private static int SellerOrder(string source)
    {
        var index = ProductSources.All.ToList().IndexOf(source);
        return index < 0 ? int.MaxValue : index;
    }
}

internal static class CustomerIdentity
{
    /// <summary>The signed-in user's id (ASP.NET Core Identity puts it in the NameIdentifier claim).</summary>
    public static Guid? CustomerId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
