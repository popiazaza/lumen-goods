using Lumen.Domain.Payments;

namespace Lumen.Api.Payments;

/// <summary>How an order is paid. One method per order attempt; never card and wallet together (BR-PAY-06).</summary>
public enum PaymentMethod
{
    Card,
    Wallet,
}

/// <summary>
/// One payment of an order (BR-PAY-01..07). It starts only by atomically claiming the order (AwaitingPayment →
/// PaymentProcessing), and at most one payment per order is not final (a partial unique index), so two tabs can never
/// both charge. Status changes go through <see cref="PaymentTransitions.Payment"/>; whether it still holds the order
/// is <see cref="PaymentWindowPolicy.IsPaymentInProgress"/> with <see cref="Snapshot"/>.
/// Card data: only brand and last 4 digits, never the full number or the CVC (BR-PAY-02; the database rejects
/// anything but 4 digits in <see cref="CardLast4"/>).
/// </summary>
public sealed class Payment
{
    /// <summary>Also the gateway idempotency key: every retry of this payment (automatic or Try again) sends the same id.</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid OrderId { get; set; }

    public PaymentMethod Method { get; set; }

    public PaymentStatus Status { get; set; } = PaymentTransitions.Payment.Initial;

    /// <summary>When the payment entered <see cref="Status"/>: <see cref="PaymentSnapshot.StatusSince"/> for the window rules.</summary>
    public DateTimeOffset StatusChangedUtc { get; set; }

    /// <summary>
    /// BR-PAY-05: this try's one automatic retry has been sent (<see cref="PaymentSnapshot.AutomaticRetryUsed"/>).
    /// Reset when the customer tries a Failed payment again.
    /// </summary>
    public bool AutomaticRetryUsed { get; set; }

    /// <summary>Which of the order's attempts this is (1-based, BR-PAY-03); a Try again of a Failed payment moves it on.</summary>
    public int AttemptNumber { get; set; } = 1;

    /// <summary>Set when Declined or Expired. The customer and admin wording comes from <see cref="DeclineMessages"/>.</summary>
    public DeclineReason? DeclineReason { get; set; }

    /// <summary>The order total at the time of payment: never an amount the client sent (BR-CHK-01).</summary>
    public decimal Amount { get; set; }

    public CardBrand? CardBrand { get; set; }

    /// <summary>Last 4 digits of the card; null for wallet payments.</summary>
    public string? CardLast4 { get; set; }

    /// <summary>When the one-time code for this attempt was issued (card 0034, BR-PAY-03).</summary>
    public DateTimeOffset? OneTimeCodeIssuedUtc { get; set; }

    /// <summary>Issued + <c>Payments:OneTimeCodeLifetime</c>, on the server clock; the payment page shows the countdown.</summary>
    public DateTimeOffset? OneTimeCodeExpiresUtc { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>When it reached an outcome (Succeeded, Declined, Failed, Expired).</summary>
    public DateTimeOffset? CompletedUtc { get; set; }

    /// <summary>PostgreSQL xmin: the background charge, the one-time code request and the expiry job race for the same row.</summary>
    public uint Version { get; set; }

    public PaymentSnapshot Snapshot => new(Status, StatusChangedUtc, AutomaticRetryUsed);
}

/// <summary>What an entry on the admin's payment timeline records.</summary>
public enum PaymentEventType
{
    /// <summary>The payment was created and claimed the order (method and amount).</summary>
    Started,

    /// <summary>A gateway call: the first try, the automatic retry (BR-PAY-05) or the customer's Try again; <c>Detail</c> says which.</summary>
    SentToGateway,

    /// <summary>The gateway asked for a one-time code (card 0034).</summary>
    OneTimeCodeRequested,
    Approved,

    /// <summary><c>Detail</c> holds the admin wording (<see cref="DeclineMessages.ForAdmin"/>), e.g. "Card reported stolen".</summary>
    Declined,

    /// <summary>The gateway failed (not a decline); <c>Detail</c> holds the error.</summary>
    GatewayError,
    Failed,

    /// <summary>The one-time code ran out.</summary>
    Expired,
    Refunded,
    RefundFailed,
}

/// <summary>One entry on the admin's payment timeline (spec: "the detail page shows payment events"). Append-only.</summary>
public sealed class PaymentEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid PaymentId { get; set; }

    /// <summary>Copied from the payment, so the order's timeline is one indexed read.</summary>
    public Guid OrderId { get; set; }

    public DateTimeOffset OccurredUtc { get; set; }

    public PaymentEventType Type { get; set; }

    /// <summary>The amount involved (charge or refund), when there is one.</summary>
    public decimal? Amount { get; set; }

    /// <summary>Admin-facing detail: the real decline reason, the gateway error, "automatic retry". Never card data.</summary>
    public string? Detail { get; set; }
}
