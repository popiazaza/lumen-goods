namespace Lumen.Domain.Payments;

/// <summary>
/// How long, and how many times, a placed order can be paid.
/// - BR-PAY-03: at most <see cref="PaymentOptions.MaxAttemptsPerOrder"/> payment attempts (card or wallet) per order;
///   the last failure expires the order. An automatic retry of the same payment id is not a new attempt.
/// - BR-STOCK-01: the order also expires when its hold runs out, but never while a payment is in progress
///   (a slow card 0042 approval that outlives the hold still makes the order Paid).
/// </summary>
public sealed class PaymentWindowPolicy(PaymentOptions options)
{
    private readonly PaymentOptions _options = DomainOptions.Validated(options);

    public int MaxAttempts => _options.MaxAttemptsPerOrder;

    public DateTimeOffset HoldEndsAt(DateTimeOffset placedAt) => placedAt + _options.OrderHold;

    public int AttemptsLeft(int failedAttempts) => Math.Max(0, _options.MaxAttemptsPerOrder - failedAttempts);

    public bool CanStartAttempt(int failedAttempts, DateTimeOffset placedAt, DateTimeOffset now) =>
        AttemptsLeft(failedAttempts) > 0 && now < HoldEndsAt(placedAt);

    /// <summary>True when the order must move to Expired (and release its reserved stock).</summary>
    public bool MustExpireOrder(int failedAttempts, DateTimeOffset placedAt, DateTimeOffset now, bool paymentInProgress) =>
        !paymentInProgress && (AttemptsLeft(failedAttempts) == 0 || now >= HoldEndsAt(placedAt));
}
