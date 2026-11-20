namespace Lumen.Domain.Orders;

/// <summary>The order's stored status. Allowed changes: <see cref="OrderTransitions.Order"/>.</summary>
public enum OrderStatus
{
    /// <summary>Placed; prices locked and own stock reserved for the hold (BR-CHK-01, BR-STOCK-01).</summary>
    AwaitingPayment,

    /// <summary>A payment has claimed the order; no second payment can start (BR-PAY-01).</summary>
    PaymentProcessing,

    /// <summary>Paid; some lines still wait for a partner. Shown to the customer as "Processing".</summary>
    Paid,
    Completed,
    PartiallyRefunded,
    Refunded,

    /// <summary>Not paid in time, or out of payment attempts; reserved stock released.</summary>
    Expired,
}

/// <summary>Each order line's own status (BR-ORD-02). Allowed changes: <see cref="OrderTransitions.Line"/>.</summary>
public enum OrderLineStatus
{
    /// <summary>The order is not paid yet.</summary>
    Pending,

    /// <summary>Paid; the partner has not confirmed yet. Shown as "Awaiting {shop}".</summary>
    AwaitingPartner,

    /// <summary>The partner accepted but gave no decision for 24 hours; an admin must look (BR-ORD-04). Shown as "Delayed".</summary>
    NeedsAttention,
    Confirmed,

    /// <summary>Not supplied; the line, its tax and possibly its shipment fee were refunded (BR-ORD-03).</summary>
    Rejected,
}

/// <summary>
/// A stored refund to the original payment method (BR-ORD-03), sent outside the database transaction that recorded
/// it. Its id is the gateway idempotency key. Allowed changes: <see cref="OrderTransitions.Refund"/>.
/// </summary>
public enum RefundStatus
{
    /// <summary>Recorded with its amount; not confirmed by the gateway (card) or the wallet yet.</summary>
    Pending,
    Succeeded,

    /// <summary>The gateway did not confirm it; retried later with the same refund id, never replaced by a new refund.</summary>
    Failed,
}

/// <summary>The order Lumen places with a partner for one line (BR-ORD-01). Allowed changes: <see cref="OrderTransitions.PartnerOrder"/>.</summary>
public enum PartnerOrderStatus
{
    /// <summary>Written in the transaction that marked the order Paid; not sent yet.</summary>
    Pending,

    /// <summary>Sent at least once; retries reuse the same reference, so a resend never orders twice.</summary>
    Submitted,

    /// <summary>Kestrel took the order (ticket RECEIVED) and will decide later.</summary>
    Accepted,
    Confirmed,
    Rejected,

    /// <summary>Not accepted within the deadline after payment (BR-ORD-04); the line is refunded.</summary>
    GivenUp,
}
