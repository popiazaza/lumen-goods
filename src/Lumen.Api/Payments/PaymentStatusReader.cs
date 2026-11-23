using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Orders;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace Lumen.Api.Payments;

/// <summary>
/// The order's current payment as the payment page shows and polls it (<see cref="PaymentStatusDto"/>). "Current" is
/// the latest payment: at most one is not final, so it is the one that may still change.
/// </summary>
internal sealed class PaymentStatusReader(
    LumenDbContext db,
    PaymentHousekeeping housekeeping,
    PaymentWindowPolicy window,
    OneTimeCodePolicy codes,
    TimeProvider time)
{
    /// <summary>
    /// Customer wording for a payment that ended Failed and the gateway could not settle yet, while the customer may
    /// try again (<see cref="PaymentRetryMode.SamePaymentOnly"/>, BR-PAY-05).
    /// </summary>
    public const string FailedMessage = "We could not confirm your payment. Try again with the same card; you will not be charged twice.";

    /// <summary>
    /// The same, once no new attempt can start (hold over or no attempts left): the customer can only wait for the
    /// gateway's answer. Charged → the order is paid; not charged → it expires (BR-PAY-05, BR-STOCK-01).
    /// </summary>
    public const string FailedAwaitingGatewayMessage = "We could not confirm your payment yet, and we are checking with the card provider. "
        + "If your card was charged, your order will be paid; if not, the order will be cancelled and you will not be charged.";

    /// <param name="settle">
    /// First bring the payment up to date (an expired code, an error past its retry window, a stuck charge, a Failed
    /// payment the gateway can settle), so a page polling for the outcome never waits for the next job run.
    /// </param>
    /// <returns>Null when the order has no payment yet.</returns>
    public async Task<PaymentStatusDto?> CurrentAsync(Guid orderId, bool settle, CancellationToken cancellationToken)
    {
        var payment = await LatestAsync(orderId, cancellationToken);
        if (payment is null)
        {
            return null;
        }

        if (settle && !PaymentTransitions.Payment.IsFinal(payment.Status))
        {
            await housekeeping.SettleIfDueAsync(payment.Id, cancellationToken);
            payment = await LatestAsync(orderId, cancellationToken);
        }

        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, cancellationToken);
        return Build(order, payment!);
    }

    public PaymentStatusDto Build(Order order, Payment payment)
    {
        var now = time.GetUtcNow();
        var needsCode = payment.Status == PaymentStatus.RequiresAction && payment.OneTimeCodeExpiresUtc > now;
        var retryMode = order.Status != OrderStatus.AwaitingPayment
            || !window.CanStartAttempt(order.FailedPaymentAttempts, order.CreatedUtc, now)
                ? PaymentRetryMode.None
                : payment.Status == PaymentStatus.Failed
                    ? PaymentRetryMode.SamePaymentOnly
                    : PaymentRetryMode.AnyMethod;

        var declineMessage = payment.Status switch
        {
            PaymentStatus.Declined or PaymentStatus.Expired => payment.DeclineReason is { } reason
                ? DeclineMessages.ForCustomer(reason)
                : DeclineMessages.GenericDecline,
            PaymentStatus.Failed => retryMode == PaymentRetryMode.SamePaymentOnly ? FailedMessage : FailedAwaitingGatewayMessage,
            _ => null,
        };

        return new PaymentStatusDto(
            payment.Id,
            order.Number,
            payment.Method,
            payment.Status,
            payment.Amount,
            payment.CardBrand,
            payment.CardLast4,
            needsCode,
            payment.OneTimeCodeExpiresUtc?.UtcDateTime,
            needsCode ? codes.TestCode : null,
            declineMessage,
            window.AttemptsLeft(order.FailedPaymentAttempts),
            retryMode != PaymentRetryMode.None,
            retryMode,
            order.Status,
            order.HoldExpiresUtc.UtcDateTime,
            now.UtcDateTime);
    }

    private Task<Payment?> LatestAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.Payments.AsNoTracking()
            .Where(p => p.OrderId == orderId)
            .OrderByDescending(p => p.CreatedUtc).ThenByDescending(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken);
}
