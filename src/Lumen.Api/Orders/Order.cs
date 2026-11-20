using Lumen.Api.Catalog;
using Lumen.Api.Payments;
using Lumen.Domain.Orders;
using Lumen.Domain.Pricing;

namespace Lumen.Api.Orders;

/// <summary>
/// One customer order across all sellers: one payment, one order (spec). Created at Place order with its prices
/// locked (BR-CHK-01) and own stock reserved (BR-STOCK-01); paid within the hold; afterwards its status follows its
/// lines (BR-ORD-02, <see cref="OrderStatusDerivation"/>). Status changes go through <see cref="OrderTransitions.Order"/>.
/// Money columns are what was charged; they never grow after payment (refunds are recorded separately).
/// </summary>
public sealed class Order
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Random, customer-facing number such as <c>LG-7K3M9QX2</c> (<see cref="OrderNumberGenerator"/>). Unique; the route key.</summary>
    public required string Number { get; set; }

    public Guid CustomerId { get; set; }

    /// <summary>
    /// BR-PAY-01: the key the checkout page sends with Place order. Unique, so a repeated Place order (double click,
    /// retry after a timeout) finds this order instead of creating a second one. Look it up together with
    /// <see cref="CustomerId"/>, so a key never reveals another customer's order.
    /// </summary>
    public Guid CheckoutKey { get; set; }

    public OrderStatus Status { get; set; } = OrderTransitions.Order.Initial;

    /// <summary>Sum of the lines' <see cref="OrderLine.LineSubtotal"/>.</summary>
    public decimal Subtotal { get; set; }

    /// <summary>Sum of the shipments' fees (BR-SHIP-01).</summary>
    public decimal ShippingTotal { get; set; }

    /// <summary>Sum of the lines' <see cref="OrderLine.Tax"/> (BR-MONEY-02).</summary>
    public decimal TaxTotal { get; set; }

    /// <summary>Subtotal + shipping + tax (a check constraint): the amount the payment charges, never one sent by the client.</summary>
    public decimal Total { get; set; }

    /// <summary>
    /// Sum of every refund recorded for the order, whatever its status: a Pending or Failed refund will still be paid
    /// out (BR-ORD-03, <see cref="PaidOrderSnapshot.AmountRefunded"/>). Never above <see cref="Total"/> (a check constraint).
    /// </summary>
    public decimal RefundedTotal { get; set; }

    public required ShippingAddress ShipTo { get; set; }

    /// <summary>The method the customer chose; it applies to Lumen's own shipment only (BR-SHIP-01). Partner shipments are Standard.</summary>
    public ShippingMethod ShippingMethodForOwnItems { get; set; }

    /// <summary>
    /// Payment attempts that ended Declined, Failed or Expired (BR-PAY-03): <see cref="Domain.Payments.PaymentWindowPolicy"/>
    /// takes it as <c>failedAttempts</c>. Counted here rather than from payment rows, because a Failed card payment's
    /// Try again reuses the same payment row (BR-PAY-05).
    /// </summary>
    public int FailedPaymentAttempts { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>When the 15-minute price and stock hold ends (BR-CHK-01, BR-STOCK-01): <c>CreatedUtc + Payments:OrderHold</c>.</summary>
    public DateTimeOffset HoldExpiresUtc { get; set; }

    public DateTimeOffset? PaidUtc { get; set; }

    /// <summary>PostgreSQL xmin, for the status changes made by requests and background jobs at the same time.</summary>
    public uint Version { get; set; }

    public List<OrderLine> Lines { get; set; } = [];

    public List<OrderShipment> Shipments { get; set; } = [];

    public List<Payment> Payments { get; set; } = [];

    public List<Refund> Refunds { get; set; } = [];
}

/// <summary>
/// The ship-to address, stored as columns on the order (an EF Core complex type). The field set is what both partner
/// order formats need: Harbor's <c>ship_to</c> as it is, Kestrel's <c>Delivery</c> with Street = "line1, line2".
/// </summary>
/// <param name="Region">US state code for the single launch country (A-51).</param>
/// <param name="Country">ISO 3166-1 alpha-2, e.g. "US".</param>
public sealed record ShippingAddress(
    string Name,
    string Line1,
    string? Line2,
    string City,
    string Region,
    string PostalCode,
    string Country);

/// <summary>
/// One product line of an order, with a snapshot of the product and its price as charged, so the order never changes
/// when the catalogue does. Its own status (BR-ORD-02, <see cref="OrderTransitions.Line"/>); a rejected line is
/// refunded with its tax (BR-ORD-03).
/// </summary>
public sealed class OrderLine
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid OrderId { get; set; }

    /// <summary>1-based position in the order; the partner reference is <c>{orderNumber}-{lineNumber}</c> (BR-ORD-01).</summary>
    public int LineNumber { get; set; }

    public Guid ProductId { get; set; }

    public Product? Product { get; set; }

    /// <summary>The seller that ships the line (a <c>ProductSources</c> value); also its <see cref="OrderShipment"/>.</summary>
    public required string Source { get; set; }

    /// <summary>The product's id at its seller (Harbor item_code, Kestrel ref, Lumen SKU): what a partner order sends.</summary>
    public required string SourceProductId { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// Source URL of the primary image when ordered (as in <see cref="ProductImage.SourceUrl"/>), or null. Never sent to
    /// the browser: order pages build a Lumen media URL from the product id.
    /// </summary>
    public string? PrimaryImageSourceUrl { get; set; }

    /// <summary>USD unit price charged (BR-MONEY-01: converted per unit and rounded to the cent).</summary>
    public decimal UnitPriceUsd { get; set; }

    public int Quantity { get; set; }

    /// <summary><see cref="UnitPriceUsd"/> × <see cref="Quantity"/>, not rounded again (a check constraint).</summary>
    public decimal LineSubtotal { get; set; }

    /// <summary>This line's own tax, rounded per line (BR-MONEY-02), so refunding the line refunds exactly its tax.</summary>
    public decimal Tax { get; set; }

    /// <summary>The unit price in the seller's currency (EUR for Kestrel), for the record. Null when unknown.</summary>
    public decimal? SourceUnitPrice { get; set; }

    public string SourceCurrency { get; set; } = "USD";

    /// <summary>The rate used to convert <see cref="SourceUnitPrice"/> (BR-MONEY-01); null for USD sellers.</summary>
    public decimal? FxRateToUsd { get; set; }

    public OrderLineStatus Status { get; set; } = OrderTransitions.Line.Initial;

    /// <summary>Why the line was not supplied or is delayed, e.g. <c>OUT_OF_STOCK:K-7731</c> or "Not accepted within 30 minutes".</summary>
    public string? StatusReason { get; set; }

    /// <summary>What was refunded for this line (subtotal + tax when rejected); never above subtotal + tax (a check constraint).</summary>
    public decimal RefundedAmount { get; set; }

    public PartnerOrder? PartnerOrder { get; set; }
}

/// <summary>
/// One seller's shipment and its fee (BR-SHIP-01): one per seller present in the order (unique). The fee is fixed when
/// the order is placed; afterwards it can only be refunded (BR-ORD-03), once.
/// </summary>
public sealed class OrderShipment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid OrderId { get; set; }

    public required string Source { get; set; }

    public ShippingMethod Method { get; set; }

    public decimal Fee { get; set; }

    /// <summary>Set when every line of this seller was rejected and the fee was refunded (<see cref="RefundCalculator"/>).</summary>
    public bool FeeRefunded { get; set; }
}
