using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Orders;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Payments;

/// <summary>
/// Brings one payment, or one unpaid order, to where the payment window rules say it must be (BR-STOCK-01, BR-PAY-03,
/// BR-PAY-05). Used by the payment window job for everything due, and by the payment status endpoint for the payment
/// it shows, so a customer never waits for the next job run to see an expired code. Gateway status calls are made
/// outside any transaction; the gateway, not Lumen's clock alone, decides a card payment's fate, so a code it approved
/// in time or a charge it completed is never lost.
/// </summary>
internal sealed class PaymentHousekeeping(
    LumenDbContext db,
    IPaymentGateway gateway,
    PaymentRecorder recorder,
    OrderPaymentWriter writer,
    PaymentWindowPolicy window,
    DetachedPaymentRunner runner,
    IOptionsMonitor<PaymentOptions> payments,
    IOptionsMonitor<PaymentJobsOptions> jobs,
    TimeProvider time,
    ILogger<PaymentHousekeeping> logger)
{
    /// <summary>A charge still Created or Processing this long has lost its worker (slow-card delay + grace).</summary>
    public TimeSpan StuckAfter => payments.CurrentValue.SlowCardDelay + jobs.CurrentValue.StaleChargeGrace;

    /// <summary>
    /// - RequiresAction whose code has run out → the gateway's status (it closes the code itself) → Expired.
    /// - Error whose automatic retry was used or can no longer run → Failed (BR-PAY-05).
    /// - Created or Processing for longer than <see cref="StuckAfter"/> with no charge running in this process → the
    ///   gateway's status decides (approved, declined, still running, or never received → Failed).
    /// - Failed → settled by the gateway's status (<see cref="PaymentRecorder.SettleFailedAsync"/>).
    /// Anything else is left alone.
    /// </summary>
    public async Task SettleIfDueAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
        if (payment is null || payment.Method != PaymentMethod.Card)
        {
            return;
        }

        var now = time.GetUtcNow();
        switch (payment.Status)
        {
            case PaymentStatus.RequiresAction when !window.IsPaymentInProgress(payment.Snapshot, now):
                if (await AskGatewayAsync(paymentId, cancellationToken) is { } answer)
                {
                    // The gateway closes a code nobody entered in time; if it still says "waiting", its code is past
                    // Lumen's expiry as well, so it is closed as expired here too.
                    await recorder.RecordAsync(paymentId, answer.Status == GatewayChargeStatus.RequiresAction
                        ? new GatewayChargeResult(GatewayChargeStatus.Declined, DeclineReason.OneTimeCodeExpired)
                        : answer, cancellationToken);
                }

                break;
            case PaymentStatus.Error when window.ClosingOutcome(payment.Snapshot, now) == PaymentStatus.Failed:
                await recorder.CloseErroredAsync(paymentId, cancellationToken);
                break;
            case PaymentStatus.Created or PaymentStatus.Processing
                when payment.StatusChangedUtc <= now - StuckAfter && !runner.IsRunning(paymentId):
                if (await AskGatewayAsync(paymentId, cancellationToken) is { } status)
                {
                    logger.LogWarning("Payment {Payment} was stuck {Status} since {Since:o}; the gateway says {Answer}",
                        paymentId, payment.Status, payment.StatusChangedUtc, status.Status);
                    await recorder.RecordAsync(paymentId, status, cancellationToken);
                }

                break;
            case PaymentStatus.Failed:
                await recorder.SettleFailedAsync(paymentId, cancellationToken);
                break;
        }
    }

    /// <summary>
    /// BR-STOCK-01: expires an order awaiting payment that <see cref="PaymentWindowPolicy.MustExpireOrder"/> says must
    /// expire (hold over or attempts used up, and no payment holding it), and releases its reserved stock. An order whose
    /// latest payment is Failed is held: that payment is settled with the gateway first (<see cref="SettleIfDueAsync"/>),
    /// because it may have charged (BR-PAY-05).
    /// </summary>
    /// <returns>True when the order was expired.</returns>
    public async Task<bool> ExpireIfDueAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await writer.LockOrderAsync(orderId, cancellationToken);
        var order = await db.Orders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null || order.Status != OrderStatus.AwaitingPayment)
        {
            return false;
        }

        var latest = await db.Payments.AsNoTracking()
            .Where(p => p.OrderId == orderId)
            .OrderByDescending(p => p.CreatedUtc).ThenByDescending(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var now = time.GetUtcNow();
        if (!window.MustExpireOrder(order.FailedPaymentAttempts, order.CreatedUtc, now, latest?.Snapshot))
        {
            return false;
        }

        order.Status = OrderTransitions.Order.Move(order.Status, OrderStatus.Expired);
        await writer.ReleaseReservationsAsync(order, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<GatewayChargeResult?> AskGatewayAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        try
        {
            return await gateway.GetChargeAsync(paymentId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Gateway status for payment {Payment} is unavailable; trying again on the next run", paymentId);
            return null;
        }
    }
}
