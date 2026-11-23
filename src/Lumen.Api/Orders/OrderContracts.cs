using System.Text.Json.Serialization;
using Lumen.Api.Checkout;
using Lumen.Api.Infrastructure.Json;
using Lumen.Api.Payments;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Lumen.Domain.Pricing;

namespace Lumen.Api.Orders;

/// <summary>
/// One row of the customer's order history (<c>GET /api/v1/orders</c>, a <c>PagedResult</c>, newest first).
/// </summary>
/// <param name="StatusText">What the customer sees: "Awaiting payment", "Processing", "Partial refund pending", "Partially refunded", ...</param>
/// <param name="ItemCount">Sum of the line quantities.</param>
/// <param name="SellerNames">The shops the items came from, e.g. ["Lumen Goods", "Kestrel Outfitters"].</param>
/// <param name="CanPay">As <see cref="OrderDetailDto.CanPay"/>: the order awaits payment, its hold lasts and attempts are left, so the list offers "Pay now".</param>
/// <param name="AwaitingOneTimeCode">The current payment waits for the customer's one-time code (not expired yet), so the list offers "Enter the code".</param>
/// <example>
/// <code>
/// { "number": "LG-7K3M9QX2", "status": "partially_refunded", "statusText": "Partially refunded",
///   "createdUtc": "2026-11-20T08:00:00Z", "total": 75.46, "refundedTotal": 22.51, "itemCount": 3,
///   "sellerNames": ["Lumen Goods", "Kestrel Outfitters"], "canPay": false, "awaitingOneTimeCode": false }
/// </code>
/// </example>
public sealed record OrderSummaryDto(
    string Number,
    OrderStatus Status,
    string StatusText,
    DateTime CreatedUtc,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Total,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal RefundedTotal,
    int ItemCount,
    IReadOnlyList<string> SellerNames,
    bool CanPay,
    bool AwaitingOneTimeCode);

/// <summary>
/// <c>GET /api/v1/orders/{number}</c> (the customer's own order; another's is 404) and the answer to Place order. Lines
/// are grouped by seller under their shipment, each with its own status (BR-ORD-02) and any refund (BR-ORD-03).
/// The confirmation and detail pages poll while <see cref="IsUpdating"/>.
/// </summary>
/// <param name="StatusText">Customer wording for <see cref="Status"/> (paid is shown as "Processing").</param>
/// <param name="HoldExpiresUtc">Until when the order can be paid (15-minute price and stock hold).</param>
/// <param name="CanPay">The order awaits payment, its hold lasts and attempts are left: show the payment step.</param>
/// <param name="IsUpdating">Something can still change on its own: a payment being confirmed, or a line awaiting its partner.</param>
/// <param name="Payment">The latest payment, or null before the first one.</param>
/// <example>
/// <code>
/// {
///   "number": "LG-7K3M9QX2", "status": "paid", "statusText": "Processing",
///   "createdUtc": "2026-11-20T08:00:00Z", "paidUtc": "2026-11-20T08:01:10Z", "holdExpiresUtc": "2026-11-20T08:15:00Z",
///   "shipments": [
///     { "source": "lumen", "sellerName": "Lumen Goods", "method": "express", "fee": 12.99, "feeRefunded": false, "lines": [
///       { "id": "0193a3d5-...", "lineNumber": 1, "productId": "0193a0f1-...", "name": "Enamel Camp Mug", "imageUrl": null,
///         "unitPrice": 18.50, "quantity": 2, "subtotal": 37.00, "tax": 2.96,
///         "status": "confirmed", "statusText": "Confirmed", "refundedAmount": 0.00 } ] },
///     { "source": "kestrel", "sellerName": "Kestrel Outfitters", "method": "standard", "fee": 4.99, "feeRefunded": false, "lines": [
///       { "id": "0193a3d6-...", "lineNumber": 2, "productId": "0193a0f2-...", "name": "Merino Trail Socks", "imageUrl": "https://shop.example/media/...",
///         "unitPrice": 16.22, "quantity": 1, "subtotal": 16.22, "tax": 1.30,
///         "status": "awaiting_partner", "statusText": "Awaiting Kestrel Outfitters", "refundedAmount": 0.00 } ] } ],
///   "totals": { "subtotal": 53.22, "shipping": 17.98, "tax": 4.26, "total": 75.46, "refunded": 0.00, "netPaid": 75.46 },
///   "shippingAddress": { "name": "Ada Lovelace", "line1": "1200 NW Naito Pkwy", "line2": null, "city": "Portland",
///                        "region": "OR", "postalCode": "97209", "country": "US" },
///   "shippingMethod": "express",
///   "payment": { "paymentId": "0193a2c4-...", "method": "card", "status": "succeeded", "amount": 75.46,
///                "cardBrand": "visa", "cardLast4": "1111", "completedUtc": "2026-11-20T08:01:10Z" },
///   "refunds": [],
///   "canPay": false, "isUpdating": true
/// }
/// </code>
/// </example>
public sealed record OrderDetailDto(
    string Number,
    OrderStatus Status,
    string StatusText,
    DateTime CreatedUtc,
    DateTime? PaidUtc,
    DateTime HoldExpiresUtc,
    IReadOnlyList<OrderShipmentDto> Shipments,
    OrderTotalsDto Totals,
    ShippingAddressDto ShippingAddress,
    ShippingMethod ShippingMethod,
    PaymentSummaryDto? Payment,
    IReadOnlyList<RefundDto> Refunds,
    bool CanPay,
    bool IsUpdating);

/// <summary>One seller's part of the order: its shipment (method, fee, whether the fee was refunded) and its lines.</summary>
public sealed record OrderShipmentDto(
    string Source,
    string SellerName,
    ShippingMethod Method,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Fee,
    bool FeeRefunded,
    IReadOnlyList<OrderLineDto> Lines);

/// <param name="Status">pending | awaiting_partner | needs_attention | confirmed | rejected.</param>
/// <param name="StatusText">
/// BR-ORD-02 wording with the seller's name: "Confirmed", "Awaiting Harbor Supply Co.", "Confirmed by Harbor Supply Co.",
/// "Delayed", "Not supplied, refund pending" (until the refund is paid back), "Not supplied, refunded $40.03".
/// </param>
/// <param name="RefundedAmount">What was refunded for this line (subtotal + tax); 0.00 when nothing was.</param>
public sealed record OrderLineDto(
    Guid Id,
    int LineNumber,
    Guid ProductId,
    string Name,
    string? ImageUrl,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal UnitPrice,
    int Quantity,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Subtotal,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Tax,
    OrderLineStatus Status,
    string StatusText,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal RefundedAmount);

/// <param name="Total">What was charged: subtotal + shipping + tax.</param>
/// <param name="Refunded">All refunds recorded for the order (shipping fees included).</param>
/// <param name="NetPaid">Total − refunded: what the customer paid in the end.</param>
public sealed record OrderTotalsDto(
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Subtotal,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Shipping,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Tax,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Total,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Refunded,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal NetPaid);

/// <summary>The order's latest payment, for "Paid by Visa ending 1111" or "Paid from your wallet".</summary>
public sealed record PaymentSummaryDto(
    Guid PaymentId,
    PaymentMethod Method,
    PaymentStatus Status,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Amount,
    CardBrand? CardBrand,
    string? CardLast4,
    DateTime? CompletedUtc);

/// <summary>A refund to the original payment method (BR-ORD-03).</summary>
/// <param name="Amount">The lines' amounts (each line's <see cref="OrderLineDto.RefundedAmount"/>) plus <see cref="ShippingAmount"/>.</param>
/// <param name="Status">pending | succeeded | failed (a failed refund is retried; it is still owed).</param>
/// <param name="LineIds">The order lines it covers (<see cref="OrderLineDto.Id"/>).</param>
/// <param name="ShippingAmount">The seller's shipping fee it pays back (every line of that seller was rejected); 0.00 when none.</param>
public sealed record RefundDto(
    Guid Id,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Amount,
    RefundStatus Status,
    string Reason,
    IReadOnlyList<Guid> LineIds,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal ShippingAmount,
    DateTime CreatedUtc,
    DateTime? CompletedUtc);
