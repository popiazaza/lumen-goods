using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Lumen.Api.Carts;
using Lumen.Api.Infrastructure.Json;
using Lumen.Api.Orders;
using Lumen.Domain.Pricing;

namespace Lumen.Api.Checkout;

/// <summary>
/// <c>GET /api/v1/checkout/preview?shippingMethod=standard|express</c>: the signed-in customer's cart priced as an
/// order would be (BR-MONEY-02, BR-SHIP-01), for the shipping-method and review steps. What the review page shows is
/// what Place order sends back: every line's <c>productId</c>, <c>quantity</c> and <c>unitPrice</c>
/// (<see cref="PlaceOrderRequest.Lines"/>, flattened from <see cref="Shipments"/>) and <see cref="Total"/>
/// (<see cref="PlaceOrderRequest.ExpectedTotal"/>). Read from Lumen's database only (product rows the last sync or a
/// Place order's live check wrote); the live partner check runs at Place order.
/// </summary>
/// <param name="Shipments">One per seller present, each with its priced lines (the review page groups lines by seller).</param>
/// <param name="ShippingMethod">The method priced here, for Lumen's own items; partner shipments are always Standard.</param>
/// <param name="ShippingOptions">The methods this cart may use: Express only when it holds Lumen's own items.</param>
/// <param name="Blockers">Lines that stop the order (not sellable, out of stock, above stock); empty when it can be placed.</param>
/// <example>
/// <code>
/// {
///   "shipments": [
///     { "source": "lumen", "sellerName": "Lumen Goods", "method": "express", "fee": 12.99, "lines": [
///       { "cartLineId": "0193a1b2-...", "productId": "0193a0f1-...", "name": "Enamel Camp Mug", "imageUrl": null,
///         "unitPrice": 18.50, "quantity": 2, "lineTotal": 37.00, "tax": 2.96 } ] },
///     { "source": "kestrel", "sellerName": "Kestrel Outfitters", "method": "standard", "fee": 4.99, "lines": [
///       { "cartLineId": "0193a1b3-...", "productId": "0193a0f2-...", "name": "Merino Trail Socks", "imageUrl": "https://shop.example/media/...",
///         "unitPrice": 16.22, "quantity": 1, "lineTotal": 16.22, "tax": 1.30 } ] } ],
///   "subtotal": 53.22, "shipping": 17.98, "tax": 4.26, "total": 75.46,
///   "shippingMethod": "express",
///   "shippingOptions": [ { "method": "standard", "fee": 4.99, "shippingTotal": 9.98 },
///                        { "method": "express", "fee": 12.99, "shippingTotal": 17.98 } ],
///   "blockers": []
/// }
/// </code>
/// </example>
public sealed record CheckoutPreviewDto(
    IReadOnlyList<CheckoutShipmentDto> Shipments,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Subtotal,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Shipping,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Tax,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Total,
    ShippingMethod ShippingMethod,
    IReadOnlyList<ShippingOptionDto> ShippingOptions,
    IReadOnlyList<CheckoutLineChangeDto> Blockers);

/// <summary>One seller's shipment in the preview: its method, its fee and the lines it ships.</summary>
public sealed record CheckoutShipmentDto(
    string Source,
    string SellerName,
    ShippingMethod Method,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Fee,
    IReadOnlyList<CheckoutLineDto> Lines);

/// <summary>
/// A priced line: <see cref="ProductId"/>, <see cref="Quantity"/> and <see cref="UnitPrice"/> go back unchanged in
/// <see cref="PlaceOrderRequest.Lines"/>. <see cref="Tax"/> is the line's own tax, rounded per line (BR-MONEY-02).
/// </summary>
/// <param name="LineTotal">Unit price × quantity, not rounded again (BR-MONEY-01).</param>
public sealed record CheckoutLineDto(
    Guid CartLineId,
    Guid ProductId,
    string Name,
    string? ImageUrl,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal UnitPrice,
    int Quantity,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal LineTotal,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Tax);

/// <param name="Fee">The fee for Lumen's own shipment with this method.</param>
/// <param name="ShippingTotal">The order's whole shipping charge if this method is chosen (all sellers' shipments).</param>
public sealed record ShippingOptionDto(
    ShippingMethod Method,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Fee,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal ShippingTotal);

/// <summary>
/// <c>POST /api/v1/checkout/orders</c> (signed in): place exactly what the customer reviewed. The server checks partner
/// lines live first (BR-CHK-02) and saves what it learns to the product rows, then re-prices the cart and compares it
/// with the request <b>per line and in total</b> (BR-CHK-01):
/// <list type="number">
/// <item>the cart's products against <see cref="Lines"/>: a product on one side only is <c>cart_changed</c>, a different
/// quantity is <c>quantity_changed</c>;</item>
/// <item>each line's current unit price against its <see cref="PlaceOrderLineDto.UnitPrice"/>: <c>price_changed</c>
/// (up or down); a line that cannot be supplied is <c>quantity_limited</c>, <c>unavailable</c> or
/// <c>temporarily_unavailable</c>;</item>
/// <item>the re-priced total (lines, shipping and tax) against <see cref="ExpectedTotal"/>.</item>
/// </list>
/// Any difference is a 409 <see cref="PriceChangedProblem"/> and nothing is created or reserved. Otherwise 201 with the
/// order (<c>OrderDetailDto</c>, Location <c>/api/v1/orders/{number}</c>), priced from exactly the values compared, own
/// stock reserved and prices held for 15 minutes. The same <see cref="CheckoutKey"/> again returns 200 with that same
/// order (BR-PAY-01); after a 409 nothing used the key, so the re-confirmed request may send it again.
/// </summary>
/// <param name="CheckoutKey">A random id the checkout page creates once and sends with every Place order attempt.</param>
/// <param name="Lines">
/// Every reviewed line (<see cref="CheckoutPreviewDto.Shipments"/>' lines, flattened); at most <see cref="MaxLines"/>
/// (50), else a 400 keyed <c>lines</c>.
/// </param>
/// <param name="ExpectedTotal">
/// The total the customer reviewed (<see cref="CheckoutPreviewDto.Total"/>). Required: a missing total is a 400 keyed
/// <c>expectedTotal</c>, never read as 0.00. Nullable only so that a missing value can be told apart; the endpoint
/// checks it (the built-in <c>[Required]</c> check would key the error by the C# name).
/// </param>
/// <example>
/// <code>
/// { "checkoutKey": "8d7c9e1a-4f7b-4b8e-9a51-3c2d1e0f9a7b",
///   "lines": [ { "productId": "0193a0f1-...", "quantity": 2, "unitPrice": 18.50 },
///              { "productId": "0193a0f2-...", "quantity": 1, "unitPrice": 16.22 } ],
///   "expectedTotal": 75.46, "shippingMethod": "express",
///   "shippingAddress": { "name": "Ada Lovelace", "line1": "1200 NW Naito Pkwy", "line2": null, "city": "Portland",
///                        "region": "OR", "postalCode": "97209", "country": "US" } }
/// </code>
/// </example>
public sealed record PlaceOrderRequest(
    [Required] Guid? CheckoutKey,
    [Required, MinLength(1)] IReadOnlyList<PlaceOrderLineDto>? Lines,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal? ExpectedTotal,
    [Required] ShippingAddressDto? ShippingAddress,
    ShippingMethod ShippingMethod = ShippingMethod.Standard)
{
    /// <summary>
    /// The most lines one order may have: as many as the largest cart (<see cref="CartOptions.MaxLinesCeiling"/>). It
    /// bounds an order's total, which the unit price cap relies on (PriceLimits.MaxUnitPriceUsd).
    /// </summary>
    public const int MaxLines = CartOptions.MaxLinesCeiling;
}

/// <summary>One reviewed line, as <see cref="CheckoutLineDto"/> showed it.</summary>
/// <param name="UnitPrice">The USD unit price the customer saw.</param>
public sealed record PlaceOrderLineDto(
    [Required] Guid? ProductId,
    [Range(1, CartLine.MaxQuantity)] int Quantity,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal UnitPrice);

/// <summary>
/// A ship-to address in the fields both partners' order formats need. The launch country is the US (A-51,
/// <c>Checkout:Country</c>); the checkout validates state and ZIP formats for it.
/// </summary>
/// <param name="Region">State code, e.g. "OR".</param>
/// <param name="Country">ISO 3166-1 alpha-2, e.g. "US".</param>
public sealed record ShippingAddressDto(
    [Required, MaxLength(100)] string Name,
    [Required, MaxLength(200)] string Line1,
    [MaxLength(200)] string? Line2,
    [Required, MaxLength(100)] string City,
    [Required, MaxLength(100)] string Region,
    [Required, MaxLength(20)] string PostalCode,
    [Required, StringLength(2, MinimumLength = 2)] string Country)
{
    public ShippingAddress ToEntity() => new(Name.Trim(), Line1.Trim(), string.IsNullOrWhiteSpace(Line2) ? null : Line2.Trim(),
        City.Trim(), Region.Trim().ToUpperInvariant(), PostalCode.Trim(), Country.Trim().ToUpperInvariant());

    public static ShippingAddressDto From(ShippingAddress address) =>
        new(address.Name, address.Line1, address.Line2, address.City, address.Region, address.PostalCode, address.Country);
}

/// <summary>
/// The 409 body of Place order when what the customer reviewed is no longer true: the cart's lines or quantities
/// differ from the request, a price moved up or down (BR-CHK-01), the total moved (shipping or tax), or a line can no
/// longer be supplied in full (BR-CHK-02). Nothing was created, reserved or charged. An RFC 9457 problem
/// (<c>application/problem+json</c>); the UI recognises it by <see cref="Type"/>.
/// <para>
/// Live partner values write through: before answering, Place order saves the partner prices and availability it
/// just read to the product rows (so the cart, the preview and the catalogue show them too) and computes
/// <see cref="NewTotal"/> from those same values; a re-confirmed Place order is priced from the same rows. Confirming
/// <see cref="NewTotal"/> therefore converges instead of looping, unless the partner changes again in between.
/// </para>
/// <para>
/// When <see cref="CanConfirm"/> (only prices or the total changed), the customer confirms the new amounts and Place
/// order is sent again with each changed line's <c>to</c> as its unit price and <see cref="NewTotal"/> as the expected
/// total (or the page re-fetches the preview, which already shows them). Otherwise the cart must change first
/// (remove or reduce the listed lines) or the review must be reloaded (<c>cart_changed</c>, <c>quantity_changed</c>).
/// </para>
/// </summary>
/// <param name="Changes">One entry per differing line; empty when only the total moved (shipping or tax).</param>
/// <param name="NewTotal">The order total now, at the saved prices; null when a line has to change first.</param>
/// <example>
/// <code>
/// {
///   "type": "urn:lumen:problem:checkout-changed", "title": "Your order changed", "status": 409,
///   "detail": "A price changed since you reviewed your order. Check the new total and confirm again.",
///   "changes": [ { "cartLineId": "0193a1b3-...", "productId": "0193a0f2-...", "name": "Merino Trail Socks",
///                  "sellerName": "Kestrel Outfitters", "kind": "price_changed", "from": 16.22, "to": 17.31,
///                  "quantityFrom": null, "quantityTo": null, "available": null,
///                  "message": "Price changed from $16.22 to $17.31" } ],
///   "newTotal": 76.63, "canConfirm": true
/// }
/// </code>
/// </example>
public sealed record PriceChangedProblem(
    string Detail,
    IReadOnlyList<CheckoutLineChangeDto> Changes,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal? NewTotal)
{
    public const string TypeUri = "urn:lumen:problem:checkout-changed";

    [JsonPropertyOrder(-3)]
    public string Type => TypeUri;

    [JsonPropertyOrder(-2)]
    public string Title => "Your order changed";

    [JsonPropertyOrder(-1)]
    public int Status => StatusCodes.Status409Conflict;

    /// <summary>True when only prices (or just the total) changed, so confirming <see cref="NewTotal"/> is enough.</summary>
    public bool CanConfirm => NewTotal is not null && Changes.All(c => c.Kind == CheckoutChangeKind.PriceChanged);
}

/// <summary>One line that differs from what the customer reviewed (or that blocks the preview).</summary>
/// <param name="CartLineId">The cart line; null for a reviewed product that is no longer in the cart (cart_changed).</param>
/// <param name="From">price_changed: the unit price reviewed (USD); otherwise null.</param>
/// <param name="To">price_changed: the unit price now (USD), already saved to the product; otherwise null.</param>
/// <param name="QuantityFrom">quantity_changed and cart_changed: the quantity reviewed (0 for a line added since); otherwise null.</param>
/// <param name="QuantityTo">quantity_changed and cart_changed: the quantity in the cart now (0 for a line removed since); otherwise null.</param>
/// <param name="Available">quantity_limited: how many can be bought now; otherwise null.</param>
/// <param name="Message">
/// Ready to show: "Price changed from $16.22 to $17.31", "Quantity changed from 2 to 3", "Added to your cart since
/// you reviewed it", "Only 1 available", "Temporarily unavailable".
/// </param>
public sealed record CheckoutLineChangeDto(
    Guid? CartLineId,
    Guid ProductId,
    string Name,
    string SellerName,
    CheckoutChangeKind Kind,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal? From,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal? To,
    int? QuantityFrom,
    int? QuantityTo,
    int? Available,
    string Message);

/// <summary>
/// Wire: price_changed | quantity_changed | cart_changed | quantity_limited | unavailable | temporarily_unavailable.
/// </summary>
public enum CheckoutChangeKind
{
    /// <summary>BR-CHK-01: the unit price is different now (up or down).</summary>
    PriceChanged,

    /// <summary>The cart holds a different quantity of this product than the request (changed in another tab).</summary>
    QuantityChanged,

    /// <summary>The product is in the cart but not in the request, or the other way round (added or removed since).</summary>
    CartChanged,

    /// <summary>BR-CHK-02: fewer units can be supplied than the line asks for.</summary>
    QuantityLimited,

    /// <summary>The product is no longer sold or is out of stock.</summary>
    Unavailable,

    /// <summary>BR-CHK-02: the partner could not be checked in time (slow or down); try again later or remove the line.</summary>
    TemporarilyUnavailable,
}
