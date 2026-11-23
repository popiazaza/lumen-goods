using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Infrastructure.Telemetry;
using Lumen.Api.Orders;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace Lumen.Api.Payments;

/// <summary>
/// Records what the gateway said about a card payment, after the gateway call and never during it (no payment I/O in a
/// transaction). Each change is one transaction over the payment and its order, guarded by their row versions: when
/// the charge worker, the one-time code request and the payment window job act on the same payment at once, one wins
/// and the others re-read and find nothing left to do. Recording the same answer twice changes nothing.
/// </summary>
internal sealed class PaymentRecorder(
    LumenDbContext db,
    IPaymentGateway gateway,
    OrderPaymentWriter writer,
    PaymentWindowPolicy window,
    OneTimeCodePolicy codes,
    TimeProvider time,
    LumenMetrics metrics,
    ILogger<PaymentRecorder> logger)
{
    private const int MaxConcurrencyRetries = 5;

    /// <returns>The payment's status afterwards.</returns>
    public Task<PaymentStatus> RecordAsync(Guid paymentId, GatewayChargeResult result, CancellationToken cancellationToken) =>
        ChangeAsync(paymentId, (order, payment, now) => ApplyAsync(order, payment, result, now, cancellationToken), cancellationToken);

    /// <summary>
    /// BR-PAY-05: claims the one automatic retry after a gateway error (Error → Processing, same payment id).
    /// False when the retry is not allowed any more (already used, too late, or the payment moved on): then no second
    /// gateway call may be made.
    /// </summary>
    public async Task<bool> StartAutomaticRetryAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var retried = false;
        await ChangeAsync(paymentId, async (_, payment, now) =>
        {
            retried = payment.Status == PaymentStatus.Error && window.ShouldRetryAutomatically(payment.Snapshot, now);
            if (!retried)
            {
                return false;
            }

            payment.Status = PaymentTransitions.Payment.Move(payment.Status, PaymentStatus.Processing);
            payment.StatusChangedUtc = now;
            payment.AutomaticRetryUsed = true;
            await writer.AddEventAsync(payment, PaymentEventType.SentToGateway, now, payment.Amount, "Automatic retry, same payment id", cancellationToken);
            return true;
        }, cancellationToken);
        return retried;
    }

    /// <summary>
    /// Closes a gateway error whose automatic retry was used or can no longer run (BR-PAY-05): Failed, and the order may
    /// be paid again with the same payment id. Does nothing while the retry may still come.
    /// </summary>
    public Task<PaymentStatus> CloseErroredAsync(Guid paymentId, CancellationToken cancellationToken) =>
        ChangeAsync(paymentId, async (order, payment, now) =>
        {
            if (window.ClosingOutcome(payment.Snapshot, now) != PaymentStatus.Failed)
            {
                return false;
            }

            await writer.RecordFailedAttemptAsync(order, payment, PaymentStatus.Failed, null,
                "Gateway error and no successful retry; the customer's Try again reuses this payment id", now, cancellationToken);
            return true;
        }, cancellationToken);

    /// <summary>
    /// BR-PAY-05: settles a Failed payment from the gateway's status for its payment id, asked outside any transaction.
    /// Charged → Succeeded and the order Paid (even after the hold: the order waited for this payment), nothing charged
    /// again. Never charged → Declined (<see cref="DeclineReason.GatewayErrorUnresolvedNotCharged"/>), final, so any method
    /// may pay the order; the failed attempt was counted when it failed, so it is not counted again. No answer (or a
    /// charge still running) → stays Failed: only a Try again with the same payment id is safe.
    /// </summary>
    /// <returns>The payment's status afterwards.</returns>
    public async Task<PaymentStatus> SettleFailedAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        GatewayChargeResult answer;
        try
        {
            answer = await gateway.GetChargeAsync(paymentId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Gateway status for failed payment {Payment} is unavailable; it stays Failed", paymentId);
            answer = new GatewayChargeResult(GatewayChargeStatus.Processing);
        }

        var charged = answer.Status == GatewayChargeStatus.Approved;
        var neverCharged = answer.Status is GatewayChargeStatus.Declined or GatewayChargeStatus.Error or GatewayChargeStatus.NotFound;
        return await ChangeAsync(paymentId, async (order, payment, now) =>
        {
            if (payment.Status != PaymentStatus.Failed || !(charged || neverCharged))
            {
                return false;
            }

            if (charged)
            {
                if (order.Status == OrderStatus.AwaitingPayment)
                {
                    order.Status = OrderTransitions.Order.Move(order.Status, OrderStatus.PaymentProcessing);
                }

                if (order.Status != OrderStatus.PaymentProcessing)
                {
                    logger.LogCritical("Failed payment {Payment} was charged, but order {Number} is {Status}; refund it manually",
                        payment.Id, order.Number, order.Status);
                    return false;
                }

                await writer.MarkPaidAsync(order, payment, now, cancellationToken);
                logger.LogInformation("Failed payment {Payment} of order {Number} was charged after all: order paid", payment.Id, order.Number);
                return true;
            }

            payment.Status = PaymentTransitions.Payment.Move(payment.Status, PaymentStatus.Declined);
            payment.StatusChangedUtc = now;
            payment.CompletedUtc = now;
            payment.DeclineReason = DeclineReason.GatewayErrorUnresolvedNotCharged;
            await writer.AddEventAsync(payment, PaymentEventType.Declined, now, null,
                DeclineMessages.ForAdmin(DeclineReason.GatewayErrorUnresolvedNotCharged), cancellationToken);
            return true;
        }, cancellationToken);
    }

    private async Task<bool> ApplyAsync(Order order, Payment payment, GatewayChargeResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (payment.Status == PaymentStatus.Created && result.Status != GatewayChargeStatus.Processing)
        {
            // A payment is saved as Processing from the start, so Created is never stored; this keeps the move along the
            // transition table if one ever is.
            payment.Status = PaymentTransitions.Payment.Move(payment.Status, PaymentStatus.Processing);
        }

        var waiting = payment.Status is PaymentStatus.Processing or PaymentStatus.RequiresAction;
        switch (result.Status)
        {
            case GatewayChargeStatus.Approved when waiting:
                await writer.MarkPaidAsync(order, payment, now, cancellationToken);
                logger.LogInformation("Order {Number} paid by card: payment {Payment}, {Amount} USD", order.Number, payment.Id, payment.Amount);
                return true;

            case GatewayChargeStatus.Approved when payment.Status != PaymentStatus.Succeeded:
                // The gateway took money for a payment Lumen already closed. With the gateway deciding code expiry
                // itself this cannot happen; if it ever does, it needs a person, not a guess.
                logger.LogCritical("Gateway approved payment {Payment} of order {Number}, which is already {Status}; refund it manually",
                    payment.Id, order.Number, payment.Status);
                return false;

            case GatewayChargeStatus.Declined when waiting:
                var reason = result.DeclineReason ?? DeclineReason.CardReportedStolen;
                var outcome = reason == DeclineReason.OneTimeCodeExpired && payment.Status == PaymentStatus.RequiresAction
                    ? PaymentStatus.Expired
                    : PaymentStatus.Declined;
                await writer.RecordFailedAttemptAsync(order, payment, outcome, reason, null, now, cancellationToken);
                logger.LogInformation("Payment {Payment} of order {Number} {Outcome}: {Reason}", payment.Id, order.Number, outcome, DeclineMessages.ForAdmin(reason));
                return true;

            case GatewayChargeStatus.RequiresAction when payment.Status == PaymentStatus.Processing:
                var issued = result.OneTimeCodeIssuedUtc ?? now;
                payment.Status = PaymentTransitions.Payment.Move(payment.Status, PaymentStatus.RequiresAction);
                payment.StatusChangedUtc = issued;
                payment.OneTimeCodeIssuedUtc = issued;
                var expires = result.OneTimeCodeExpiresUtc ?? codes.ExpiresAt(issued);
                payment.OneTimeCodeExpiresUtc = expires;
                // A length, not a clock time: the admin page shows the event's own time in the viewer's time zone.
                // Counted from that time (now), not from when the gateway issued the code, so the two add up.
                await writer.AddEventAsync(payment, PaymentEventType.OneTimeCodeRequested, now, null,
                    $"One-time code issued; valid for {Validity(expires - now)}", cancellationToken);
                return true;

            case GatewayChargeStatus.Error or GatewayChargeStatus.NotFound when payment.Status == PaymentStatus.Processing:
                payment.Status = PaymentTransitions.Payment.Move(payment.Status, PaymentStatus.Error);
                payment.StatusChangedUtc = now;
                var error = result.Status == GatewayChargeStatus.NotFound
                    ? "The gateway has no record of this charge: nothing was charged"
                    : result.Error ?? "Gateway error";
                await writer.AddEventAsync(payment, PaymentEventType.GatewayError, now, null, error, cancellationToken);

                // A charge the gateway never saw cannot be retried without the card (never stored), and an error after the
                // retry ends the attempt: both close as Failed now. Otherwise the one automatic retry comes next.
                if (result.Status == GatewayChargeStatus.NotFound || !window.IsPaymentInProgress(payment.Snapshot, now))
                {
                    await writer.RecordFailedAttemptAsync(order, payment, PaymentStatus.Failed, null,
                        "Gateway error and no successful retry; the customer's Try again reuses this payment id", now, cancellationToken);
                }

                return true;

            default:
                // Processing (still running), or an answer for a payment that has already moved on: nothing to record.
                return false;
        }
    }

    /// <summary>
    /// Locks the order, loads the payment and the order (with lines) tracked, lets <paramref name="change"/> modify them,
    /// and saves in one transaction. Every writer of an order's payment state takes the order's row lock first (then
    /// product rows), so they queue instead of deadlocking; the row versions stay as a second guard.
    /// </summary>
    private async Task<PaymentStatus> ChangeAsync(
        Guid paymentId, Func<Order, Payment, DateTimeOffset, Task<bool>> change, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            var orderId = await db.Payments.Where(p => p.Id == paymentId).Select(p => p.OrderId).SingleAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await writer.LockOrderAsync(orderId, cancellationToken);
            var payment = await db.Payments.SingleAsync(p => p.Id == paymentId, cancellationToken);
            var before = payment.Status;
            var order = await db.Orders.Include(o => o.Lines).SingleAsync(o => o.Id == orderId, cancellationToken);
            if (!await change(order, payment, time.GetUtcNow()))
            {
                return payment.Status;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                metrics.PaymentChanged(before, payment.Status);
                return payment.Status;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyRetries)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                logger.LogDebug("Payment {Payment} changed concurrently; re-reading it", paymentId);
            }
        }
    }

    /// <summary>"5 minutes", "4 minutes 20 seconds", "1 second"; a window already past reads "0 seconds".</summary>
    internal static string Validity(TimeSpan validity)
    {
        var seconds = Math.Max(0, (long)Math.Round(validity.TotalSeconds, MidpointRounding.AwayFromZero));
        static string Unit(long n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
        return (seconds / 60, seconds % 60) switch
        {
            (0, var s) => Unit(s, "second"),
            (var m, 0) => Unit(m, "minute"),
            (var m, var s) => $"{Unit(m, "minute")} {Unit(s, "second")}",
        };
    }
}
