using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Orders;
using Lumen.Api.Wallets;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Lumen.Api.Payments;

/// <summary>What a Pay request did.</summary>
internal abstract record PayOutcome
{
    /// <summary>
    /// The payment started (card: charging in the background), or finished at once (wallet; a Failed payment the
    /// gateway said was charged after all): 202 with the current payment.
    /// </summary>
    public sealed record Started(Guid OrderId) : PayOutcome;

    public sealed record OrderNotFound : PayOutcome;

    /// <summary>400: keyed <c>card.number</c>, <c>card.expiry</c>, <c>card.cvc</c>, <c>card.name</c>, <c>method</c>, <c>card</c>.</summary>
    public sealed record Invalid(Dictionary<string, string[]> Errors) : PayOutcome;

    /// <summary>409 <see cref="PaymentInProgressProblem"/>: a payment for this order is already running (BR-PAY-01).</summary>
    public sealed record InProgress(Guid OrderId) : PayOutcome;

    /// <summary>409: paid already, expired, hold over, or out of attempts.</summary>
    public sealed record NotPayable(string Detail) : PayOutcome;

    /// <summary>
    /// 409: the last card payment ended Failed and the gateway cannot tell yet whether it charged, so only that
    /// payment, with that card, may be tried again (BR-PAY-05, <see cref="PaymentRetryMode.SamePaymentOnly"/>).
    /// </summary>
    public sealed record SamePaymentOnly(string Detail) : PayOutcome;
}

/// <summary>
/// Starts a payment for an order (BR-PAY-01..07).
/// - BR-PAY-02: the card is validated on the server first; an invalid card never reaches the gateway, and only brand
///   and last 4 digits are stored.
/// - BR-PAY-05: a Failed payment is settled first by the gateway's status for it: charged → the order is paid and
///   nothing new is charged; never charged → it is closed and this request goes ahead with any method; unknown → only
///   the same payment id, with the same card, may be tried again.
/// - BR-PAY-01 (b): the payment starts only by atomically claiming the order (AwaitingPayment → PaymentProcessing, one
///   conditional update, while the hold lasts and attempts are left), so one payment runs per order and a paid order
///   cannot be paid again.
/// - BR-CHK-01: the amount is always the order total; the request carries none.
/// - BR-PAY-06: the wallet pays in the claiming transaction itself: one conditional debit (balance ≥ total) plus its
///   wallet transaction and the order's paid writes, or a decline that leaves the balance untouched.
/// </summary>
internal sealed class PaymentService(
    LumenDbContext db,
    OrderPaymentWriter writer,
    PaymentRecorder recorder,
    PaymentWindowPolicy window,
    DetachedPaymentRunner runner,
    WalletService wallets,
    TimeProvider time,
    ILogger<PaymentService> logger)
{
    public async Task<PayOutcome> PayAsync(Guid customerId, string orderNumber, PayRequest request, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Number == orderNumber && o.CustomerId == customerId, cancellationToken);
        if (order is null)
        {
            return new PayOutcome.OrderNotFound();
        }

        var now = time.GetUtcNow();
        var (card, errors) = Validate(request, now);
        if (errors.Count > 0)
        {
            return new PayOutcome.Invalid(errors);
        }

        if (WhyNotPayable(order, now) is { } refusal)
        {
            return refusal;
        }

        // BR-PAY-05: a Failed charge may have gone through; the gateway's status decides before anything else starts.
        var failed = await db.Payments.AsNoTracking()
            .SingleOrDefaultAsync(p => p.OrderId == order.Id && p.Status == PaymentStatus.Failed, cancellationToken);
        if (failed is not null)
        {
            var settled = await recorder.SettleFailedAsync(failed.Id, cancellationToken);
            if (settled == PaymentStatus.Succeeded)
            {
                return new PayOutcome.Started(order.Id);
            }

            if (settled == PaymentStatus.Declined)
            {
                failed = null;
            }
            else if (card is null || card.Brand != failed.CardBrand || card.Last4 != failed.CardLast4)
            {
                return new PayOutcome.SamePaymentOnly(
                    $"We could not confirm your payment with {failed.CardBrand} ending {failed.CardLast4}. Try again with that card; you will not be charged twice.");
            }
        }

        if (request.Method == PaymentMethod.Wallet)
        {
            // BR-PAY-06: a wallet opens on first use, so a customer whose registration could not open it can still pay.
            await wallets.EnsureOpenAsync(customerId, cancellationToken);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var claimedStatus = OrderTransitions.Order.Move(OrderStatus.AwaitingPayment, OrderStatus.PaymentProcessing);
        var maxAttempts = window.MaxAttempts;
        var claimed = await db.Orders
            .Where(o => o.Id == order.Id && o.Status == OrderStatus.AwaitingPayment && o.HoldExpiresUtc > now && o.FailedPaymentAttempts < maxAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, claimedStatus), cancellationToken);
        if (claimed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            var current = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id, cancellationToken);
            return WhyNotPayable(current, now) ?? new PayOutcome.InProgress(order.Id);
        }

        // The claim holds the order's row lock until commit: nothing else can change its payment state meanwhile.
        // Re-read from the database (not from entities a settlement above left tracked), so the claim is what it sees.
        db.ChangeTracker.Clear();
        var tracked = await db.Orders.Include(o => o.Lines).SingleAsync(o => o.Id == order.Id, cancellationToken);
        var payment = await StartPaymentAsync(tracked, failed?.Id, request.Method!.Value, card, now, cancellationToken);
        if (payment.Method == PaymentMethod.Wallet)
        {
            await PayFromWalletAsync(tracked, payment, now, cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // IX_Payments_OrderId_NotFinal: another payment is still open. The claim makes this unreachable; the index
            // is the last line of defence, and it means the same thing to the customer.
            await transaction.RollbackAsync(CancellationToken.None);
            logger.LogWarning(ex, "Order {Number}: second open payment refused by the database", order.Number);
            return new PayOutcome.InProgress(order.Id);
        }

        if (card is not null)
        {
            runner.StartCardCharge(payment.Id, card);
        }

        return new PayOutcome.Started(order.Id);
    }

    private async Task<Payment> StartPaymentAsync(
        Order order, Guid? failedPaymentId, PaymentMethod method, CardNumber? card, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Payment payment;
        string sentDetail;
        if (failedPaymentId is { } id)
        {
            // Try again of a Failed payment the gateway could not settle: same row, same id, so the gateway answers this
            // charge at most once (BR-PAY-05).
            payment = await db.Payments.SingleAsync(p => p.Id == id, cancellationToken);
            payment.CompletedUtc = null;
            payment.DeclineReason = null;
            payment.AutomaticRetryUsed = false;
            sentDetail = "Try again by the customer, same payment id";
        }
        else
        {
            payment = new Payment
            {
                OrderId = order.Id,
                Method = method,
                Status = PaymentTransitions.Payment.Initial,
                StatusChangedUtc = now,
                Amount = order.Total,
                CardBrand = card?.Brand,
                CardLast4 = card?.Last4,
                CreatedUtc = now,
            };
            db.Payments.Add(payment);
            await writer.AddEventAsync(payment, PaymentEventType.Started, now, payment.Amount,
                card is null ? "Wallet payment" : $"Card payment, {card}", cancellationToken);
            sentDetail = "First try";
        }

        payment.AttemptNumber = order.FailedPaymentAttempts + 1;
        payment.Status = PaymentTransitions.Payment.Move(payment.Status, PaymentStatus.Processing);
        payment.StatusChangedUtc = now;
        if (method == PaymentMethod.Card)
        {
            await writer.AddEventAsync(payment, PaymentEventType.SentToGateway, now, payment.Amount, sentDetail, cancellationToken);
        }

        return payment;
    }

    /// <summary>BR-PAY-06: one conditional debit; the balance never goes below zero and is untouched by a decline.</summary>
    private async Task PayFromWalletAsync(Order order, Payment payment, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var amount = payment.Amount;
        var balanceAfter = (await db.Database.SqlQuery<decimal>($"""
            UPDATE "Wallets" SET "Balance" = "Balance" - {amount}
            WHERE "CustomerId" = {order.CustomerId} AND "Balance" >= {amount}
            RETURNING "Balance" AS "Value"
            """).ToListAsync(cancellationToken)).Cast<decimal?>().SingleOrDefault();

        if (balanceAfter is not { } balance)
        {
            await writer.RecordFailedAttemptAsync(order, payment, PaymentStatus.Declined, DeclineReason.InsufficientWalletBalance, null, now, cancellationToken);
            logger.LogInformation("Order {Number}: wallet balance below {Amount} USD; declined", order.Number, amount);
            return;
        }

        db.WalletTransactions.Add(new WalletTransaction
        {
            CustomerId = order.CustomerId,
            Amount = -amount,
            BalanceAfter = balance,
            Kind = WalletTransactionKind.Payment,
            OrderId = order.Id,
            PaymentId = payment.Id,
            CreatedUtc = now,
        });
        await writer.MarkPaidAsync(order, payment, now, cancellationToken);
        logger.LogInformation("Order {Number} paid from the wallet: {Amount} USD, balance now {Balance}", order.Number, amount, balance);
    }

    private PayOutcome? WhyNotPayable(Order order, DateTimeOffset now) => order.Status switch
    {
        OrderStatus.PaymentProcessing => new PayOutcome.InProgress(order.Id),
        OrderStatus.AwaitingPayment when window.AttemptsLeft(order.FailedPaymentAttempts) == 0 =>
            new PayOutcome.NotPayable("This order has no payment attempts left."),
        OrderStatus.AwaitingPayment when now >= order.HoldExpiresUtc =>
            new PayOutcome.NotPayable("The time to pay for this order has run out. Your cart still has the items."),
        OrderStatus.AwaitingPayment => null,
        OrderStatus.Expired => new PayOutcome.NotPayable("This order has expired. Your cart still has the items."),
        _ => new PayOutcome.NotPayable("This order is already paid."),
    };

    private static (CardNumber? Card, Dictionary<string, string[]> Errors) Validate(PayRequest request, DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        switch (request.Method)
        {
            case PaymentMethod.Wallet:
                if (request.Card is not null)
                {
                    errors["card"] = ["Leave the card out when paying from the wallet."];
                }

                return (null, errors);
            case PaymentMethod.Card when request.Card is null:
                errors["card"] = ["Enter the card details."];
                return (null, errors);
            case PaymentMethod.Card:
                var result = CardValidator.Validate(request.Card.ToDomain(), now);
                foreach (var (field, message) in result.Errors)
                {
                    errors[field == "holderName" ? "card.name" : $"card.{field}"] = [message];
                }

                return (result.IsValid ? result.Card : null, errors);
            default:
                errors["method"] = ["Choose card or wallet."];
                return (null, errors);
        }
    }
}
