namespace Lumen.Domain.Payments;

/// <summary>
/// One payment for an order. Allowed changes: <see cref="PaymentTransitions.Payment"/>. Whether a payment is still
/// in progress (and so holds the order) is decided by <see cref="PaymentWindowPolicy.IsPaymentInProgress"/>.
/// </summary>
public enum PaymentStatus
{
    /// <summary>Written when the payment claimed the order; the gateway call is about to be sent.</summary>
    Created,

    /// <summary>Sent to the gateway (or the wallet debit is running); a charge may happen at any moment (BR-PAY-01).</summary>
    Processing,

    /// <summary>Waiting for the customer's one-time code (card 0034, BR-PAY-03), until the code expires.</summary>
    RequiresAction,
    Succeeded,
    Declined,

    /// <summary>The gateway failed (not a decline); the one automatic retry with the same payment id is still to come (BR-PAY-05).</summary>
    Error,

    /// <summary>
    /// The automatic retry errored too (or never ran). Lumen cannot tell whether the charge went through, so before
    /// anything else is tried the gateway is asked for this payment's status (BR-PAY-05): charged → Succeeded; never
    /// charged → Declined (<see cref="DeclineReason.GatewayErrorUnresolvedNotCharged"/>), final, so a new payment with
    /// any method may start. While the gateway cannot tell, only a Try again of this same payment id is safe: the
    /// gateway's idempotency returns the first result.
    /// </summary>
    Failed,

    /// <summary>The one-time code ran out before a valid one arrived.</summary>
    Expired,
}

public static class PaymentTransitions
{
    /// <summary>
    /// BR-PAY-01: the payment id is the gateway idempotency key, so a retry (Error → Processing, Failed → Processing)
    /// can never charge twice. BR-PAY-03: a one-time code approves, declines or expires the payment. BR-PAY-05: an error
    /// is retried once automatically; if that errors too the payment is Failed, and the gateway's status for it decides
    /// the way out: charged → Succeeded, never charged → Declined (then any method may pay the order), unknown → Try
    /// again with the same payment id. Every non-final status has a way out, so an abandoned payment never holds an
    /// order forever (BR-STOCK-01).
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
        new(PaymentStatus.RequiresAction, PaymentStatus.Expired, "code expired, late or never entered"),
        new(PaymentStatus.Error, PaymentStatus.Processing, "automatic retry, same payment id"),
        new(PaymentStatus.Error, PaymentStatus.Failed, "retry errored too, or never ran"),
        new(PaymentStatus.Failed, PaymentStatus.Processing, "Try again by the customer, same payment id"),
        new(PaymentStatus.Failed, PaymentStatus.Succeeded, "gateway status: it was charged"),
        new(PaymentStatus.Failed, PaymentStatus.Declined, "gateway status: never charged"),
    ]);
}
