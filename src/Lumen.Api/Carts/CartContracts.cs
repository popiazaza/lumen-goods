using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Lumen.Api.Catalog;
using Lumen.Api.Infrastructure.Json;
using Lumen.Domain.Catalog;

namespace Lumen.Api.Carts;

/// <summary>
/// <c>GET /api/v1/cart</c> and the answer to every cart change: the whole cart, priced by the server at current prices
/// (BR-CART-02). An empty cart has no lines and zero totals. Money always has two decimals; enums are snake_case.
/// </summary>
/// <param name="ItemCount">Sum of the line quantities, for the mini-cart badge (A-46), not the number of lines.</param>
/// <param name="Totals">Preview with Standard shipping for each seller present; checkout prices the real order.</param>
/// <param name="CanCheckout">
/// False when the cart is empty, any line <see cref="CartLineDto.BlocksCheckout"/>, or it holds more than
/// <see cref="MaxLines"/> lines (BR-CART-03).
/// </param>
/// <param name="MaxLines">
/// The most different products the cart may hold (<see cref="CartOptions.MaxLines"/>). Only a sign-in merge can leave
/// more; then checkout is blocked until lines are removed.
/// </param>
/// <example>
/// <code>
/// {
///   "lines": [{
///     "id": "0193a1b2-...", "product": { "id": "0193a0f1-...", "source": "harbor", "sellerName": "Harbor Supply Co.",
///       "name": "Canvas Tote", "imageUrl": "https://shop.example/media/0193a0f1-.../0?v=3f2a9c1b7d4e",
///       "availability": { "status": "low_stock", "quantity": 2 }, "sellable": true },
///     "quantity": 4, "maxQuantity": 2, "unitPrice": 54.90, "unitPriceSeen": 49.90, "lineTotal": 219.60,
///     "notices": { "priceChanged": { "from": 49.90, "to": 54.90 },
///                  "availabilityChanged": { "from": "in_stock", "to": "low_stock" },
///                  "unavailable": null, "quantityLimited": { "requested": 4, "allowed": 2 } },
///     "blocksCheckout": true, "addedUtc": "2026-11-20T08:00:00Z" }],
///   "itemCount": 4,
///   "totals": { "subtotal": 219.60, "shipping": 4.99, "tax": 17.57, "total": 242.16, "isEstimate": true },
///   "canCheckout": false,
///   "maxLines": 50
/// }
/// </code>
/// </example>
public sealed record CartDto(
    IReadOnlyList<CartLineDto> Lines,
    int ItemCount,
    CartTotalsDto Totals,
    bool CanCheckout,
    int MaxLines = CartOptions.MaxLinesCeiling)
{
    public static readonly CartDto Empty = new([], 0, CartTotalsDto.Zero, false);
}

/// <param name="MaxQuantity">
/// Most units this line may have now: 10 (BR-CART-03), or fewer when the known available quantity is lower; 0 when the
/// product cannot be bought. The quantity selector's upper bound.
/// </param>
/// <param name="UnitPrice">Current USD unit price; null when the product no longer has a price.</param>
/// <param name="UnitPriceSeen">The price the customer last saw for this line (when added or last acknowledged).</param>
/// <param name="LineTotal">Current unit price × quantity; null when there is no current price.</param>
/// <param name="BlocksCheckout">
/// The line must be removed or reduced before checkout: no longer sellable, out of stock, or more than the known
/// available quantity (BR-CART-03). Acknowledging does not clear it.
/// </param>
public sealed record CartLineDto(
    Guid Id,
    CartProductDto Product,
    int Quantity,
    int MaxQuantity,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal? UnitPrice,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal UnitPriceSeen,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal? LineTotal,
    CartLineNoticesDto Notices,
    bool BlocksCheckout,
    DateTime AddedUtc);

/// <summary>The product as the cart shows it now (current availability and sellability, not the snapshot).</summary>
/// <param name="Source">lumen | harbor | kestrel.</param>
/// <param name="SellerName">The shop's display name ("Harbor Supply Co."), shown on every partner line.</param>
/// <param name="ImageUrl">Absolute Lumen media URL of the primary image, or null when the product has none.</param>
public sealed record CartProductDto(
    Guid Id,
    string Source,
    string SellerName,
    string Name,
    string? ImageUrl,
    AvailabilityDto Availability,
    bool Sellable);

/// <summary>
/// BR-CART-02 notices: each member is null when it does not apply. <see cref="PriceChanged"/> and
/// <see cref="AvailabilityChanged"/> (and a quantity capped by a merge) stay until the customer acknowledges them
/// (<c>POST /cart/lines/{id}/acknowledge</c>); <see cref="Unavailable"/> stays while it is true.
/// </summary>
/// <param name="PriceChanged">The current price differs from the one seen, up or down.</param>
/// <param name="AvailabilityChanged">Availability got worse than when seen (in_stock → low_stock → out_of_stock).</param>
/// <param name="Unavailable">The product can no longer be bought; the line blocks checkout until removed.</param>
/// <param name="QuantityLimited">
/// The line holds, or asked for, more than allowed: a guest-cart merge capped it (BR-CART-01), or stock dropped
/// below the quantity ("Only 2 left", which blocks checkout until reduced).
/// </param>
public sealed record CartLineNoticesDto(
    PriceChangedNoticeDto? PriceChanged,
    AvailabilityChangedNoticeDto? AvailabilityChanged,
    CartLineUnavailableReason? Unavailable,
    QuantityLimitedNoticeDto? QuantityLimited)
{
    public static readonly CartLineNoticesDto None = new(null, null, null, null);
}

/// <summary>"Price changed from $49.90 to $54.90".</summary>
public sealed record PriceChangedNoticeDto(
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal From,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal To);

/// <summary>Written as the catalogue's availability strings: in_stock | low_stock | out_of_stock.</summary>
public sealed record AvailabilityChangedNoticeDto(AvailabilityStatus From, AvailabilityStatus To);

/// <param name="Requested">What the customer asked for (or the line holds).</param>
/// <param name="Allowed">What they can have: the known available quantity or 10, whichever is lower.</param>
public sealed record QuantityLimitedNoticeDto(int Requested, int Allowed);

/// <summary>Why a cart line cannot be bought (wire: no_longer_available | out_of_stock).</summary>
public enum CartLineUnavailableReason
{
    /// <summary>Deactivated, discontinued, dropped by its partner, or without a price: "No longer available".</summary>
    NoLongerAvailable,

    /// <summary>Still sold, but none in stock: "Out of stock".</summary>
    OutOfStock,
}

/// <summary>
/// Cart totals at current prices, with Standard shipping per seller present (BR-SHIP-01) and per-line tax
/// (BR-MONEY-02). Lines that are <see cref="CartLineNoticesDto.Unavailable"/> are left out; a line above the available
/// quantity counts at its current quantity until reduced.
/// </summary>
public sealed record CartTotalsDto(
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Subtotal,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Shipping,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Tax,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Total)
{
    public static readonly CartTotalsDto Zero = new(0m, 0m, 0m, 0m);

    /// <summary>
    /// Always true: the cart shows shipping, tax and total as estimates ("Estimated shipping"), because shipping assumes
    /// Standard for every seller and partner prices are checked again at checkout, which prices the real order.
    /// </summary>
    public bool IsEstimate => true;
}

/// <summary>
/// <c>POST /api/v1/cart/lines</c>: add a product, or add to its existing line. 400 when the quantity is not 1 to 10;
/// 422 <see cref="CartProblems"/> when the product is unknown, not sellable or out of stock, or the line would exceed 10
/// or the known available quantity (BR-CART-03).
/// </summary>
/// <example><code>{ "productId": "0193a0f1-...", "quantity": 2 }</code></example>
public sealed record AddCartLineRequest(
    [Required] Guid? ProductId,
    [Range(1, CartLine.MaxQuantity, ErrorMessage = CartProblems.QuantityRangeMessage)] int Quantity = 1);

/// <summary>
/// <c>PUT /api/v1/cart/lines/{lineId}</c>: set the line's quantity (1 to 10; remove the line with DELETE). Raising it
/// above the known available quantity, or raising a line that cannot be bought, is a 422; lowering is always allowed.
/// </summary>
/// <example><code>{ "quantity": 3 }</code></example>
public sealed record UpdateCartLineRequest(
    [Range(1, CartLine.MaxQuantity, ErrorMessage = CartProblems.QuantityRangeMessage)] int Quantity);
