using Lumen.Domain.Payments;

namespace Lumen.Api.Payments;

/// <summary>The mock gateway's own view of a charge.</summary>
public enum MockGatewayChargeStatus
{
    /// <summary>Accepted and still running (card 0042 answers after the slow-card delay).</summary>
    Processing,

    /// <summary>Waiting for the one-time code (card 0034).</summary>
    RequiresAction,
    Approved,
    Declined,

    /// <summary>Errored (card 0059's first call); the same idempotency key may be sent again.</summary>
    Error,
}

/// <summary>
/// The in-process mock gateway's record of one charge, keyed by the caller's idempotency key (the payment id). It plays
/// the external gateway, so Lumen's tables never reference it and it references none of them: a repeated call with
/// the same key returns this record instead of charging again (BR-PAY-01 (c)), and 0059's "error first, then approve
/// on the same payment" needs it to remember the first call (BR-PAY-05).
/// </summary>
public sealed class MockGatewayCharge
{
    /// <summary>The payment id the caller sent.</summary>
    public Guid IdempotencyKey { get; set; }

    public CardBrand CardBrand { get; set; }

    public required string CardLast4 { get; set; }

    /// <summary>How this card behaves (<see cref="CardBehaviours"/>), fixed on the first call.</summary>
    public CardBehaviour Behaviour { get; set; }

    public MockGatewayChargeStatus Status { get; set; }

    public DeclineReason? DeclineReason { get; set; }

    public decimal Amount { get; set; }

    /// <summary>How many calls with this key errored (card 0059 errors once).</summary>
    public int ErrorCount { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? CompletedUtc { get; set; }

    /// <summary>Total refunded on this charge; never above <see cref="Amount"/> (a check constraint).</summary>
    public decimal RefundedAmount { get; set; }

    public uint Version { get; set; }
}

/// <summary>
/// The mock gateway's record of one refund, keyed by the caller's idempotency key (the refund id), so a retried
/// refund returns the first result instead of paying out twice.
/// </summary>
public sealed class MockGatewayRefund
{
    /// <summary>The refund id the caller sent.</summary>
    public Guid IdempotencyKey { get; set; }

    /// <summary>The charge refunded (its <see cref="MockGatewayCharge.IdempotencyKey"/>).</summary>
    public Guid ChargeIdempotencyKey { get; set; }

    public decimal Amount { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
}
