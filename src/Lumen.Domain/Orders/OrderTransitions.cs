namespace Lumen.Domain.Orders;

/// <summary>
/// The lifecycles of an order, its lines and its partner orders. Diagrams: <c>src/Lumen.Domain/Orders/README.md</c>
/// (a unit test keeps them equal to these tables).
/// </summary>
public static class OrderTransitions
{
    /// <summary>
    /// BR-PAY-01: a payment starts only by claiming the order (AwaitingPayment → PaymentProcessing), so one payment
    /// runs per order and a paid order can never be paid again.
    /// BR-STOCK-01: an order expires only when no payment is in progress (hold over, or the last attempt failed).
    /// BR-ORD-02: after payment the status follows the lines (<see cref="OrderStatusDerivation"/>).
    /// </summary>
    public static readonly TransitionTable<OrderStatus> Order = new(OrderStatus.AwaitingPayment,
    [
        new(OrderStatus.AwaitingPayment, OrderStatus.PaymentProcessing, "payment started (atomic claim)"),
        new(OrderStatus.AwaitingPayment, OrderStatus.Expired, "hold over, no payment in progress or Failed"),
        new(OrderStatus.PaymentProcessing, OrderStatus.Paid, "payment succeeded"),
        new(OrderStatus.PaymentProcessing, OrderStatus.AwaitingPayment, "payment failed, can retry"),
        new(OrderStatus.PaymentProcessing, OrderStatus.Expired, "payment failed, no attempts or hold left"),
        new(OrderStatus.Paid, OrderStatus.Completed, "every line confirmed"),
        new(OrderStatus.Paid, OrderStatus.PartiallyRefunded, "all lines final, some refunded"),
        new(OrderStatus.Paid, OrderStatus.Refunded, "every line refunded"),
    ]);

    /// <summary>
    /// BR-ORD-02: each line has its own status. Own lines are confirmed on payment; partner lines wait for the partner.
    /// BR-ORD-03: a rejected line is refunded in the same step. BR-ORD-04: a line accepted but undecided after 24 h
    /// needs an admin, and is not refunded automatically.
    /// </summary>
    public static readonly TransitionTable<OrderLineStatus> Line = new(OrderLineStatus.Pending,
    [
        new(OrderLineStatus.Pending, OrderLineStatus.Confirmed, "own item, order paid"),
        new(OrderLineStatus.Pending, OrderLineStatus.AwaitingPartner, "partner item, order paid"),
        new(OrderLineStatus.AwaitingPartner, OrderLineStatus.Confirmed, "partner confirmed"),
        new(OrderLineStatus.AwaitingPartner, OrderLineStatus.Rejected, "partner rejected or given up, refunded"),
        new(OrderLineStatus.AwaitingPartner, OrderLineStatus.NeedsAttention, "accepted, no decision after 24 h"),
        new(OrderLineStatus.NeedsAttention, OrderLineStatus.Confirmed, "partner or admin confirmed"),
        new(OrderLineStatus.NeedsAttention, OrderLineStatus.Rejected, "partner or admin rejected, refunded"),
    ]);

    /// <summary>
    /// BR-ORD-01: partner orders exist only after payment and are sent by a background job; every retry reuses the
    /// same reference. Harbor answers synchronously (Submitted → Confirmed or Rejected); Kestrel first returns a
    /// ticket (Accepted = RECEIVED) and decides later. BR-ORD-04: one never accepted within the deadline is given
    /// up; an accepted one is never given up automatically, because the goods may still ship. When an admin resolves
    /// its line, the partner order is closed in the same step (<see cref="PartnerOrderUpdates.ResolveByAdmin"/>). One
    /// sent with an unknown outcome (Submitted) goes to Confirmed or GivenUp when an admin confirms or rejects its line.
    /// </summary>
    public static readonly TransitionTable<PartnerOrderStatus> PartnerOrder = new(PartnerOrderStatus.Pending,
    [
        new(PartnerOrderStatus.Pending, PartnerOrderStatus.Submitted, "sent, same reference on every retry"),
        new(PartnerOrderStatus.Pending, PartnerOrderStatus.GivenUp, "not accepted within 30 min"),
        new(PartnerOrderStatus.Submitted, PartnerOrderStatus.Confirmed, "Harbor created the order, or an admin confirmed the line"),
        new(PartnerOrderStatus.Submitted, PartnerOrderStatus.Accepted, "Kestrel ticket RECEIVED"),
        new(PartnerOrderStatus.Submitted, PartnerOrderStatus.Rejected, "Harbor 400, 409 or 422, Kestrel 400"),
        new(PartnerOrderStatus.Submitted, PartnerOrderStatus.GivenUp, "not accepted within 30 min, or an admin rejected the line"),
        new(PartnerOrderStatus.Accepted, PartnerOrderStatus.Confirmed, "Kestrel CONFIRMED"),
        new(PartnerOrderStatus.Accepted, PartnerOrderStatus.Rejected, "Kestrel REJECTED"),
        new(PartnerOrderStatus.Accepted, PartnerOrderStatus.ClosedByAdmin, "admin resolved the line"),
    ]);

    /// <summary>
    /// BR-ORD-03: a refund is recorded (Pending) in the transaction that rejects the line, then sent to the gateway
    /// outside it. The refund id is the gateway idempotency key, so a retry (Failed → Pending → sent again) can never
    /// pay the customer twice. A wallet refund is also sent later, not in the recording transaction: the credit and
    /// Pending → Succeeded commit together, in a transaction of their own.
    /// </summary>
    public static readonly TransitionTable<RefundStatus> Refund = new(RefundStatus.Pending,
    [
        new(RefundStatus.Pending, RefundStatus.Succeeded, "gateway or wallet confirmed"),
        new(RefundStatus.Pending, RefundStatus.Failed, "gateway error or no answer"),
        new(RefundStatus.Failed, RefundStatus.Pending, "retried with the same refund id"),
    ]);
}
