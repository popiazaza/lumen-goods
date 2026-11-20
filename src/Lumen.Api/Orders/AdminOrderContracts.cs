using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Lumen.Api.Infrastructure.Json;
using Lumen.Api.Payments;
using Lumen.Domain.Orders;

namespace Lumen.Api.Orders;

/// <summary>
/// One row of the admin orders list (<c>GET /api/v1/admin/orders?status=&amp;from=&amp;to=&amp;page=&amp;pageSize=</c>,
/// a <c>PagedResult</c>, newest first; <c>from</c>/<c>to</c> are dates, filtering on when the order was placed).
/// </summary>
/// <param name="NeedsAttention">A line waits for an admin decision (BR-ORD-04): accepted by its partner, no decision for 24 hours.</param>
/// <param name="PaymentMethod">The latest payment's method; null before any payment.</param>
/// <example>
/// <code>
/// { "number": "LG-7K3M9QX2", "status": "paid", "statusText": "Processing", "createdUtc": "2026-11-20T08:00:00Z",
///   "customerEmail": "customer@lumengoods.test", "total": 75.46, "refundedTotal": 0.00,
///   "sellerNames": ["Lumen Goods", "Kestrel Outfitters"], "paymentMethod": "card", "needsAttention": false }
/// </code>
/// </example>
public sealed record AdminOrderSummaryDto(
    string Number,
    OrderStatus Status,
    string StatusText,
    DateTime CreatedUtc,
    string CustomerEmail,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Total,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal RefundedTotal,
    IReadOnlyList<string> SellerNames,
    PaymentMethod? PaymentMethod,
    bool NeedsAttention);

/// <summary>
/// <c>GET /api/v1/admin/orders/{number}</c>: the customer's view of the order (<see cref="Order"/>) plus what only
/// the admin sees: the payment timeline with the real decline reasons (BR-PAY-07) and each partner order's status.
/// </summary>
/// <param name="FailedPaymentAttempts">Attempts that ended declined, failed or expired (5 allowed, BR-PAY-03).</param>
/// <param name="PaymentEvents">Every payment event of the order, oldest first (e.g. gateway_error then approved for card 0059).</param>
/// <param name="PartnerOrders">One per partner line, in line order.</param>
/// <example>
/// <code>
/// {
///   "order": { "number": "LG-7K3M9QX2", "status": "paid", "...": "as OrderDetailDto" },
///   "customerEmail": "customer@lumengoods.test",
///   "failedPaymentAttempts": 0,
///   "paymentEvents": [
///     { "occurredUtc": "2026-11-20T08:01:00Z", "paymentId": "0193a2c4-...", "type": "started", "amount": 75.46, "detail": "Card: visa ending 0059" },
///     { "occurredUtc": "2026-11-20T08:01:01Z", "paymentId": "0193a2c4-...", "type": "gateway_error", "amount": null, "detail": "Gateway error" },
///     { "occurredUtc": "2026-11-20T08:01:03Z", "paymentId": "0193a2c4-...", "type": "sent_to_gateway", "amount": 75.46, "detail": "Automatic retry" },
///     { "occurredUtc": "2026-11-20T08:01:04Z", "paymentId": "0193a2c4-...", "type": "approved", "amount": 75.46, "detail": null } ],
///   "partnerOrders": [
///     { "id": "0193a4e7-...", "orderLineId": "0193a3d6-...", "lineNumber": 2, "source": "kestrel", "sellerName": "Kestrel Outfitters",
///       "externalRef": "LG-7K3M9QX2-2", "partnerOrderId": "KT-5521", "status": "accepted", "reason": null, "attempts": 3,
///       "nextAttemptUtc": "2026-11-20T08:02:10Z", "lastError": null, "submittedUtc": "2026-11-20T08:01:15Z",
///       "acceptedUtc": "2026-11-20T08:01:16Z", "decidedUtc": null, "needsAttention": false } ]
/// }
/// </code>
/// </example>
public sealed record AdminOrderDto(
    OrderDetailDto Order,
    string CustomerEmail,
    int FailedPaymentAttempts,
    IReadOnlyList<PaymentEventDto> PaymentEvents,
    IReadOnlyList<PartnerOrderDto> PartnerOrders);

/// <param name="Type">started | sent_to_gateway | one_time_code_requested | approved | declined | gateway_error | failed | expired | refunded | refund_failed.</param>
/// <param name="Detail">Admin wording: the real decline reason ("Card reported stolen"), the gateway error, which retry.</param>
public sealed record PaymentEventDto(
    DateTime OccurredUtc,
    Guid PaymentId,
    PaymentEventType Type,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal? Amount,
    string? Detail);

/// <summary>A partner order as the admin order page shows it (BR-ORD-01, BR-ORD-04).</summary>
/// <param name="Status">pending | submitted | accepted | confirmed | rejected | given_up | closed_by_admin.</param>
/// <param name="NeedsAttention">Its line needs an admin decision; resolve it with <see cref="ResolveOrderLineRequest"/>.</param>
public sealed record PartnerOrderDto(
    Guid Id,
    Guid OrderLineId,
    int LineNumber,
    string Source,
    string SellerName,
    string ExternalRef,
    string? PartnerOrderId,
    PartnerOrderStatus Status,
    string? Reason,
    int Attempts,
    DateTime? NextAttemptUtc,
    string? LastError,
    DateTime? SubmittedUtc,
    DateTime? AcceptedUtc,
    DateTime? DecidedUtc,
    bool NeedsAttention);

/// <summary>
/// <c>POST /api/v1/admin/orders/{number}/lines/{lineId}/resolve</c> (BR-ORD-04): an admin confirms a line that needs
/// attention (the goods are coming) or rejects it (it is refunded, BR-ORD-03). Its partner order is closed, so polling
/// stops. 409 when the line does not need attention. Answers with the updated <see cref="AdminOrderDto"/>.
/// </summary>
/// <param name="Decision">confirmed | rejected.</param>
/// <example><code>{ "decision": "rejected", "note": "Kestrel support: item lost in the warehouse" }</code></example>
public sealed record ResolveOrderLineRequest(
    [Required] OrderLineStatus? Decision,
    [MaxLength(500)] string? Note);
