using Lumen.Domain.Orders;

namespace Lumen.Domain.Payments;

/// <summary>What the payment window rules need to know about the order's current payment.</summary>
/// <param name="StatusSince">
/// When the payment entered its current status. For RequiresAction that is when the one-time code was issued; for
/// Error, when the gateway error came back.
/// </param>
/// <param name="AutomaticRetryUsed">
/// The current try's one automatic retry has been sent. Reset when the customer tries again after Failed.
/// </param>
public sealed record PaymentSnapshot(PaymentStatus Status, DateTimeOffset StatusSince, bool AutomaticRetryUsed = false);

/// <summary>
/// How long, and how many times, a placed order can be paid, and when a payment stops holding the order.
/// - BR-PAY-03: at most <see cref="PaymentOptions.MaxAttemptsPerOrder"/> attempts per order (one attempt = one
///   customer submission; its automatic retry is part of it); the last failure expires the order.
/// - BR-STOCK-01: the order expires when its hold runs out, but never while a payment is in progress, so a slow
///   card 0042 approval that outlives the hold still makes the order Paid.
/// - "In progress" is decided here, not by the caller, and every in-progress state ends on its own: an abandoned
///   one-time code or a retry that never ran stops holding the order (and its reserved stock).
/// </summary>
public sealed class PaymentWindowPolicy(PaymentOptions options)
{
    private readonly PaymentOptions _options = DomainOptions.Validated(options);

    public int MaxAttempts => _options.MaxAttemptsPerOrder;

    public DateTimeOffset HoldEndsAt(DateTimeOffset placedAt) => placedAt + _options.OrderHold;

    public int AttemptsLeft(int failedAttempts) => Math.Max(0, _options.MaxAttemptsPerOrder - failedAttempts);

    public bool CanStartAttempt(int failedAttempts, DateTimeOffset placedAt, DateTimeOffset now) =>
        AttemptsLeft(failedAttempts) > 0 && now < HoldEndsAt(placedAt);

    /// <summary>
    /// True while a charge may still happen for this payment:
    /// Created and Processing always (a gateway call is under way); RequiresAction until its one-time code expires;
    /// Error while its one automatic retry is still to come (not used yet, and within
    /// <see cref="PaymentOptions.AutomaticRetryWindow"/>). Outcomes (Succeeded, Declined, Failed, Expired) never are.
    /// </summary>
    public bool IsPaymentInProgress(PaymentSnapshot payment, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payment);
        return payment.Status switch
        {
            PaymentStatus.Created or PaymentStatus.Processing => true,
            PaymentStatus.RequiresAction => now < payment.StatusSince + _options.OneTimeCodeLifetime,
            PaymentStatus.Error => !payment.AutomaticRetryUsed && now < payment.StatusSince + _options.AutomaticRetryWindow,
            _ => false,
        };
    }

    /// <summary>BR-PAY-05: after a gateway error, retry once automatically with the same payment id, and only once.</summary>
    public bool ShouldRetryAutomatically(PaymentSnapshot payment, DateTimeOffset now) =>
        payment.Status == PaymentStatus.Error && IsPaymentInProgress(payment, now);

    /// <summary>
    /// The outcome that closes a payment which is no longer in progress but has none yet: an abandoned or late
    /// one-time code → Expired; an error whose retry errored too, or never ran in time → Failed.
    /// Null while the payment is still in progress or already has its outcome.
    /// </summary>
    public PaymentStatus? ClosingOutcome(PaymentSnapshot payment, DateTimeOffset now) =>
        IsPaymentInProgress(payment, now)
            ? null
            : payment.Status switch
            {
                PaymentStatus.RequiresAction => PaymentStatus.Expired,
                PaymentStatus.Error => PaymentStatus.Failed,
                _ => null,
            };

    /// <summary>
    /// The order status that follows a payment outcome. Succeeded → Paid, even after the hold ended (the payment was
    /// in progress, so the order was waiting for it). Declined, Failed or Expired → AwaitingPayment while the hold and
    /// the attempts left allow another try; otherwise Expired, which releases the reserved stock.
    /// </summary>
    /// <param name="failedAttemptsBefore">Failed attempts recorded on the order before this outcome.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="outcome"/> is not an outcome (the payment is still running).</exception>
    public OrderStatus NextOrderStatusAfter(PaymentStatus outcome, int failedAttemptsBefore, DateTimeOffset placedAt, DateTimeOffset now) =>
        outcome switch
        {
            PaymentStatus.Succeeded => OrderStatus.Paid,
            PaymentStatus.Declined or PaymentStatus.Failed or PaymentStatus.Expired =>
                CanStartAttempt(failedAttemptsBefore + 1, placedAt, now) ? OrderStatus.AwaitingPayment : OrderStatus.Expired,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a payment outcome: the payment is still running."),
        };

    /// <summary>
    /// True when an order awaiting payment must move to Expired (and release its reserved stock): no payment in progress,
    /// and the hold is over or no attempts are left. Close an abandoned payment first with <see cref="ClosingOutcome"/>.
    /// </summary>
    /// <param name="currentPayment">The order's latest payment, or null when it has none.</param>
    public bool MustExpireOrder(int failedAttempts, DateTimeOffset placedAt, DateTimeOffset now, PaymentSnapshot? currentPayment) =>
        (currentPayment is null || !IsPaymentInProgress(currentPayment, now))
        && (AttemptsLeft(failedAttempts) == 0 || now >= HoldEndsAt(placedAt));
}
