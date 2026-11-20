namespace Lumen.Domain.Payments;

/// <summary>One payment attempt for an order. Allowed changes: <see cref="PaymentTransitions.Payment"/>.</summary>
public enum PaymentStatus
{
    Created,

    /// <summary>Sent to the gateway (or the wallet debit is running); the order is claimed (BR-PAY-01).</summary>
    Processing,

    /// <summary>Waiting for the customer's one-time code (card 0034, BR-PAY-03).</summary>
    RequiresAction,
    Succeeded,
    Declined,

    /// <summary>The gateway failed (not a decline); retried with the same payment id (BR-PAY-05).</summary>
    Error,

    /// <summary>The one-time code ran out before a valid one arrived.</summary>
    Expired,
}

public static class PaymentTransitions
{
    /// <summary>
    /// BR-PAY-01: the payment id is the gateway idempotency key, so a retry (Error → Processing) can never charge twice.
    /// BR-PAY-03: a one-time code approves, declines or expires the attempt. BR-PAY-05: an error is retried once
    /// automatically, then on the customer's Try again, always with the same payment id.
    /// </summary>
    public static readonly TransitionTable<PaymentStatus> Payment = new(PaymentStatus.Created,
    [
        new(PaymentStatus.Created, PaymentStatus.Processing, "sent to the gateway"),
        new(PaymentStatus.Processing, PaymentStatus.Succeeded, "approved"),
        new(PaymentStatus.Processing, PaymentStatus.Declined, "declined"),
        new(PaymentStatus.Processing, PaymentStatus.RequiresAction, "one-time code needed"),
        new(PaymentStatus.Processing, PaymentStatus.Error, "gateway error, not a decline"),
        new(PaymentStatus.RequiresAction, PaymentStatus.Succeeded, "right code in time"),
        new(PaymentStatus.RequiresAction, PaymentStatus.Declined, "wrong code"),
        new(PaymentStatus.RequiresAction, PaymentStatus.Expired, "code expired"),
        new(PaymentStatus.Error, PaymentStatus.Processing, "retried with the same payment id"),
    ]);
}
