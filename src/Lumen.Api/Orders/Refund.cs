using Lumen.Domain.Orders;

namespace Lumen.Api.Orders;

/// <summary>
/// A refund to the original payment method (BR-ORD-03): recorded (Pending, with its amount from
/// <see cref="RefundCalculator"/>) in the transaction that rejects the lines, then sent outside it. Wallet refunds
/// credit the balance in the recording transaction. Status changes go through <see cref="OrderTransitions.Refund"/>.
/// </summary>
public sealed class Refund
{
    /// <summary>Also the gateway's (and the wallet's) idempotency key: a retried refund can never pay out twice.</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid OrderId { get; set; }

    /// <summary>The successful payment being refunded (card or wallet).</summary>
    public Guid PaymentId { get; set; }

    /// <summary>Line subtotals + their tax + any shipping fees refunded; always above zero (a check constraint).</summary>
    public decimal Amount { get; set; }

    public RefundStatus Status { get; set; } = OrderTransitions.Refund.Initial;

    /// <summary>Why, for the customer and the admin, e.g. "Not supplied by Kestrel Outfitters (OUT_OF_STOCK:K-7731)".</summary>
    public required string Reason { get; set; }

    /// <summary>The order lines this refund covers (a jsonb array of line ids).</summary>
    public List<Guid> LineIds { get; set; } = [];

    /// <summary>Gateway calls made so far; a Failed refund is retried with the same id.</summary>
    public int Attempts { get; set; }

    /// <summary>When the refund job should send (Pending) or retry (Failed) it; null when nothing is due.</summary>
    public DateTimeOffset? NextAttemptUtc { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? CompletedUtc { get; set; }

    public uint Version { get; set; }
}
