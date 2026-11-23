using Lumen.Domain.Orders;

namespace Lumen.Api.Orders;

/// <summary>
/// A refund to the original payment method (BR-ORD-03): recorded (Pending, with its parts from
/// <see cref="RefundCalculator"/>) in the transaction that rejects the lines, then sent outside it. Wallet refunds
/// too: the refund job credits the balance in a transaction of its own. Status changes go through
/// <see cref="OrderTransitions.Refund"/>.
/// <para>
/// What it pays back is in rows the database keeps unique, so nothing is ever refunded twice, however often a
/// rejection is seen or a job retried: each <see cref="RefundLine"/> names an order line (unique across all refunds),
/// and <see cref="ShipmentId"/> names the shipment whose fee it refunds (unique across all refunds).
/// <see cref="Amount"/> is always the lines plus <see cref="ShippingAmount"/> (checked by the database at commit).
/// One refund covers at most one shipment fee: when lines of several sellers are rejected at once, record one refund
/// per seller.
/// </para>
/// </summary>
public sealed class Refund
{
    /// <summary>Also the gateway's (and the wallet's) idempotency key: a retried refund can never pay out twice.</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid OrderId { get; set; }

    /// <summary>The successful payment being refunded (card or wallet); a payment of the same order (composite foreign key).</summary>
    public Guid PaymentId { get; set; }

    /// <summary>Sum of <see cref="Lines"/> plus <see cref="ShippingAmount"/>; always above zero.</summary>
    public decimal Amount { get; set; }

    /// <summary>The shipment whose fee this refund pays back (every line of that seller was rejected), or null.</summary>
    public Guid? ShipmentId { get; set; }

    /// <summary>The shipment fee refunded: above zero when <see cref="ShipmentId"/> is set, otherwise 0.</summary>
    public decimal ShippingAmount { get; set; }

    public RefundStatus Status { get; set; } = OrderTransitions.Refund.Initial;

    /// <summary>Why, for the customer and the admin, e.g. "Not supplied by Kestrel Outfitters (OUT_OF_STOCK:K-7731)".</summary>
    public required string Reason { get; set; }

    /// <summary>The order lines this refund pays back, each with its subtotal + tax.</summary>
    public List<RefundLine> Lines { get; set; } = [];

    /// <summary>Gateway calls made so far; a Failed refund is retried with the same id.</summary>
    public int Attempts { get; set; }

    /// <summary>When the refund job should send (Pending) or retry (Failed) it; null when nothing is due.</summary>
    public DateTimeOffset? NextAttemptUtc { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? CompletedUtc { get; set; }

    public uint Version { get; set; }
}

/// <summary>
/// One order line paid back by a refund (BR-ORD-03). <see cref="OrderLineId"/> is unique across all refunds, so a line
/// is refunded at most once; the line belongs to the refund's order (composite foreign keys on OrderId).
/// </summary>
public sealed class RefundLine
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid RefundId { get; set; }

    /// <summary>The refund's order; ties the refund and the line to the same order.</summary>
    public Guid OrderId { get; set; }

    public Guid OrderLineId { get; set; }

    /// <summary>The line's subtotal + its own tax (<see cref="LineRefund.Amount"/>); never negative.</summary>
    public decimal Amount { get; set; }
}
