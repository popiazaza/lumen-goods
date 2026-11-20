using Lumen.Api.Catalog;
using Lumen.Domain.Catalog;
using Lumen.Domain.Money;

namespace Lumen.Api.Carts;

/// <summary>A cart line weighed against its product as it is now (see <see cref="CartRules.Evaluate"/>).</summary>
/// <param name="UnitPrice">Current USD unit price, rounded to cents; null when the product has no price.</param>
/// <param name="MaxQuantity">Most units the line may hold now (0 when the product cannot be bought).</param>
/// <param name="Unavailable">Why the product cannot be bought now; null when it can.</param>
/// <param name="BlocksCheckout">Unavailable, or more units than <paramref name="MaxQuantity"/>.</param>
public sealed record CartLineState(
    Availability Availability,
    bool Sellable,
    decimal? UnitPrice,
    int MaxQuantity,
    CartLineUnavailableReason? Unavailable,
    CartLineNoticesDto Notices,
    bool BlocksCheckout);

/// <summary>
/// The cart's rules in one place, so the cart page, the merge and checkout's blockers agree:
/// - BR-CART-02: a line keeps the price, availability and sellability seen when added; a different price (up or down),
///   a worse availability or a product no longer sellable shows a notice until acknowledged. Totals use current prices.
/// - BR-CART-03: 1 to 10 units per line and never more than the known available quantity; products that are not
///   sellable or out of stock cannot be added; a line that became unavailable stays, flagged, and blocks checkout.
/// - BR-CART-01: merging a guest line into a saved line sums the quantities, capped at the known available quantity and 10.
/// </summary>
public static class CartRules
{
    /// <summary>BR-CART-03: why the product cannot be bought now, or null when it can.</summary>
    public static CartLineUnavailableReason? UnavailableReason(Product product, Availability availability) =>
        !product.IsSellable ? CartLineUnavailableReason.NoLongerAvailable
        : availability.Status == AvailabilityStatus.OutOfStock ? CartLineUnavailableReason.OutOfStock
        : null;

    /// <summary>
    /// BR-CART-03: the most units a line of this product may hold now: 10, or the known available quantity when lower;
    /// 0 when the product cannot be bought. An unknown quantity (a partner that only says "available") caps at 10.
    /// </summary>
    public static int MaxQuantity(Product product, Availability availability) =>
        UnavailableReason(product, availability) is not null
            ? 0
            : Math.Min(CartLine.MaxQuantity, availability.Quantity ?? CartLine.MaxQuantity);

    public static Availability AvailabilityOf(Product product, AvailabilityPolicy policy) =>
        policy.Describe(product.AvailableQuantity, product.StatusWhenQuantityUnknown);

    /// <summary>The current unit price as the cart shows and charges it (whole cents), or null without a price.</summary>
    public static decimal? CurrentUnitPrice(Product product) =>
        product.PriceUsd is { } price ? MoneyMath.RoundToCents(price) : null;

    /// <summary>BR-CART-02 and BR-CART-03 for one line: its notices, its quantity limit and whether it blocks checkout.</summary>
    public static CartLineState Evaluate(CartLine line, Product product, AvailabilityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(product);
        var availability = AvailabilityOf(product, policy);
        var unavailable = UnavailableReason(product, availability);
        var maxQuantity = MaxQuantity(product, availability);
        var unitPrice = CurrentUnitPrice(product);

        // Up or down: a customer must never pay a price they have not seen (and a drop is good news worth showing).
        var priceChanged = unitPrice is { } now && now != line.UnitPriceSeenUsd
            ? new PriceChangedNoticeDto(line.UnitPriceSeenUsd, now)
            : null;

        var availabilityChanged = Severity(availability.Status) > Severity(line.AvailabilitySeen)
            ? new AvailabilityChangedNoticeDto(line.AvailabilitySeen, availability.Status)
            : null;

        // Stock below the line's quantity ("Only 2 left") blocks checkout until reduced, so it outranks a merge cap the
        // customer has not acknowledged yet. An unavailable line says so instead; there is no "allowed" to offer.
        var aboveStock = unavailable is null && line.Quantity > maxQuantity;
        var quantityLimited = aboveStock
            ? new QuantityLimitedNoticeDto(line.Quantity, maxQuantity)
            : line.QuantityLimitedFrom is { } requested ? new QuantityLimitedNoticeDto(requested, line.Quantity) : null;

        return new CartLineState(
            availability,
            product.IsSellable,
            unitPrice,
            maxQuantity,
            unavailable,
            new CartLineNoticesDto(priceChanged, availabilityChanged, unavailable, quantityLimited),
            BlocksCheckout: unavailable is not null || aboveStock);
    }

    /// <summary>
    /// BR-CART-03: whether the line of this product may go from <paramref name="currentQuantity"/> to
    /// <paramref name="newQuantity"/> units (adding, or raising the quantity). Null when it may, otherwise the problem.
    /// Lowering a quantity is never checked: it can only make a line more buyable.
    /// </summary>
    public static CartProblem? CheckRaise(Product product, AvailabilityPolicy policy, int currentQuantity, int newQuantity)
    {
        var availability = AvailabilityOf(product, policy);
        switch (UnavailableReason(product, availability))
        {
            case CartLineUnavailableReason.NoLongerAvailable:
                return CartProblems.NotSellable(product);
            case CartLineUnavailableReason.OutOfStock:
                return CartProblems.OutOfStock(product);
        }

        // The limit that actually applies, so the answer names it: the known stock when it is below 10, otherwise 10.
        var maxQuantity = MaxQuantity(product, availability);
        if (newQuantity <= maxQuantity)
        {
            return null;
        }

        return maxQuantity < CartLine.MaxQuantity
            ? CartProblems.OnlyAvailable(maxQuantity, currentQuantity)
            : CartProblems.MaximumPerItem(currentQuantity);
    }

    /// <summary>
    /// BR-CART-01: the merged line's quantity when a guest line joins a saved line of the same product: the sum, capped
    /// at the known available quantity and 10. When capped, <c>LimitedFrom</c> is the sum, for the "quantity limited"
    /// notice. A product that cannot be bought now is only capped at 10: its line stays flagged and blocks checkout anyway.
    /// </summary>
    public static (int Quantity, int? LimitedFrom) MergedQuantity(int savedQuantity, int guestQuantity, Product product, AvailabilityPolicy policy)
    {
        var requested = savedQuantity + guestQuantity;
        var availability = AvailabilityOf(product, policy);
        var cap = UnavailableReason(product, availability) is null ? MaxQuantity(product, availability) : CartLine.MaxQuantity;
        var quantity = Math.Min(requested, cap);
        return (quantity, requested > quantity ? requested : null);
    }

    /// <summary>
    /// BR-CART-02: acknowledging a line accepts what changed: the seen price, availability and sellability become the
    /// current ones and a merge cap is forgotten, so the notices go away until the next change. "Unavailable" and
    /// "Only N left" describe the product now, so they stay while true.
    /// </summary>
    public static void Acknowledge(CartLine line, Product product, AvailabilityPolicy policy, DateTimeOffset nowUtc)
    {
        line.UnitPriceSeenUsd = CurrentUnitPrice(product) ?? line.UnitPriceSeenUsd;
        line.AvailabilitySeen = AvailabilityOf(product, policy).Status;
        line.SellableSeen = product.IsSellable;
        line.QuantityLimitedFrom = null;
        line.NoticeAcknowledgedUtc = nowUtc;
    }

    /// <summary>in stock → low stock → out of stock, ranked explicitly rather than by the enum's declaration order.</summary>
    private static int Severity(AvailabilityStatus status) => status switch
    {
        AvailabilityStatus.InStock => 0,
        AvailabilityStatus.LowStock => 1,
        AvailabilityStatus.OutOfStock => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
