using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Orders;
using Lumen.Domain.Catalog;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace Lumen.Api.Payments;

/// <summary>
/// The database writes a payment outcome makes to its payment and order. Every method runs inside the caller's
/// transaction, on a tracked payment and a tracked order with its lines, and moves statuses only through the domain
/// tables, so a payment and its order can never disagree.
/// </summary>
internal sealed class OrderPaymentWriter(LumenDbContext db, PaymentWindowPolicy window, ILogger<OrderPaymentWriter> logger)
{
    /// <summary>
    /// Takes the order's row lock for the rest of the transaction. Everything that changes an order's payment state
    /// (the claim, recording an outcome, expiry) locks the order before any product row, so they queue, never deadlock.
    /// </summary>
    public Task LockOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "Orders" WHERE "Id" = {orderId} FOR UPDATE""", cancellationToken);

    /// <summary>
    /// The payment succeeded: order Paid; own stock reservations become sales (BR-STOCK-01); own lines Confirmed, partner
    /// lines AwaitingPartner with one Pending partner order each, reference <c>{orderNumber}-{lineNumber}</c> (BR-ORD-01:
    /// written here, in the transaction that marks the order paid, never before); the order status derived from its lines
    /// (BR-ORD-02); and the bought products leave the customer's cart.
    /// </summary>
    public async Task MarkPaidAsync(Order order, Payment payment, DateTimeOffset now, CancellationToken cancellationToken)
    {
        payment.Status = PaymentTransitions.Payment.Move(payment.Status, PaymentStatus.Succeeded);
        payment.StatusChangedUtc = now;
        payment.CompletedUtc = now;
        payment.DeclineReason = null;
        await AddEventAsync(payment, PaymentEventType.Approved, now, payment.Amount,
            payment.Method == PaymentMethod.Wallet ? "Paid from the wallet" : $"Approved by the gateway ({payment.CardBrand} ending {payment.CardLast4})",
            cancellationToken);

        order.Status = OrderTransitions.Order.Move(order.Status, OrderStatus.Paid);
        order.PaidUtc = now;

        // Product id order, so two orders selling the same products lock them in the same order.
        foreach (var line in order.Lines.Where(l => l.Source == ProductSources.Lumen).OrderBy(l => l.ProductId))
        {
            var quantity = line.Quantity;
            var sold = await db.Database.ExecuteSqlAsync($"""
                UPDATE "Products"
                SET "OnHand" = "OnHand" - {quantity}, "Reserved" = "Reserved" - {quantity}
                WHERE "Id" = {line.ProductId} AND "Reserved" >= {quantity} AND "OnHand" >= {quantity}
                """, cancellationToken);
            if (sold != 1)
            {
                // The charge has happened: record it whatever the stock says, and raise the alarm instead.
                logger.LogCritical("Order {Number} line {Line}: no reservation of {Quantity} to turn into a sale for product {Product}",
                    order.Number, line.LineNumber, quantity, line.ProductId);
            }
        }

        foreach (var line in order.Lines.OrderBy(l => l.LineNumber))
        {
            if (line.Source == ProductSources.Lumen)
            {
                line.Status = OrderTransitions.Line.Move(line.Status, OrderLineStatus.Confirmed);
                continue;
            }

            line.Status = OrderTransitions.Line.Move(line.Status, OrderLineStatus.AwaitingPartner);
            db.PartnerOrders.Add(new PartnerOrder
            {
                OrderLineId = line.Id,
                Source = line.Source,
                ExternalRef = $"{order.Number}-{line.LineNumber}",
                Status = OrderTransitions.PartnerOrder.Initial,
                CreatedUtc = now,
                NextAttemptUtc = now,
            });
        }

        order.Status = OrderTransitions.Order.MoveIfChanged(order.Status,
            OrderStatusDerivation.Derive(order.Status, [.. order.Lines.Select(l => l.Status)]));

        var bought = order.Lines.Select(l => l.ProductId).ToList();
        var customerId = order.CustomerId;
        await db.CartLines
            .Where(l => bought.Contains(l.ProductId) && db.Carts.Any(c => c.Id == l.CartId && c.CustomerId == customerId))
            .ExecuteDeleteAsync(cancellationToken);
        await db.Carts.Where(c => c.CustomerId == customerId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedUtc, now), cancellationToken);
    }

    /// <summary>
    /// The attempt ended without a confirmed charge (Declined, Expired, or Failed: outcome unknown). BR-PAY-03: it counts
    /// as one of the order's attempts. <see cref="PaymentWindowPolicy.NextOrderStatusAfter"/> decides the order's status:
    /// back to awaiting payment, or expired with its reserved stock released (BR-STOCK-01). A Failed attempt never
    /// expires the order (BR-PAY-05): it may have charged, and the payment window job asks the gateway.
    /// </summary>
    public async Task RecordFailedAttemptAsync(
        Order order, Payment payment, PaymentStatus outcome, DeclineReason? reason, string? detail, DateTimeOffset now, CancellationToken cancellationToken)
    {
        payment.Status = PaymentTransitions.Payment.Move(payment.Status, outcome);
        payment.StatusChangedUtc = now;
        payment.CompletedUtc = now;
        payment.DeclineReason = reason;
        var (type, text) = outcome switch
        {
            PaymentStatus.Declined => (PaymentEventType.Declined, reason is { } r ? DeclineMessages.ForAdmin(r) : detail),
            PaymentStatus.Expired => (PaymentEventType.Expired, reason is { } r ? DeclineMessages.ForAdmin(r) : detail),
            _ => (PaymentEventType.Failed, detail),
        };
        await AddEventAsync(payment, type, now, null, text, cancellationToken);

        var next = window.NextOrderStatusAfter(outcome, order.FailedPaymentAttempts, order.CreatedUtc, now);
        order.FailedPaymentAttempts++;
        order.Status = OrderTransitions.Order.Move(order.Status, next);
        if (order.Status == OrderStatus.Expired)
        {
            await ReleaseReservationsAsync(order, cancellationToken);
        }
    }

    /// <summary>BR-STOCK-01: the expired order's own stock is available again.</summary>
    public async Task ReleaseReservationsAsync(Order order, CancellationToken cancellationToken)
    {
        foreach (var line in order.Lines.Where(l => l.Source == ProductSources.Lumen).OrderBy(l => l.ProductId))
        {
            var quantity = line.Quantity;
            var released = await db.Database.ExecuteSqlAsync($"""
                UPDATE "Products" SET "Reserved" = "Reserved" - {quantity}
                WHERE "Id" = {line.ProductId} AND "Reserved" >= {quantity}
                """, cancellationToken);
            if (released != 1)
            {
                logger.LogError("Order {Number} line {Line}: no reservation of {Quantity} to release for product {Product}",
                    order.Number, line.LineNumber, quantity, line.ProductId);
            }
        }

        logger.LogInformation("Order {Number} expired; reserved stock released", order.Number);
    }

    /// <summary>
    /// Appends to the payment's admin timeline. Entries of one payment are strictly ordered even when several are
    /// written at the same instant (one transaction, or a stopped test clock): each is at least 1 µs (PostgreSQL's
    /// precision) after the previous one.
    /// </summary>
    public async Task AddEventAsync(
        Payment payment, PaymentEventType type, DateTimeOffset now, decimal? amount, string? detail, CancellationToken cancellationToken)
    {
        var previous = db.ChangeTracker.Entries<PaymentEvent>()
            .Where(e => e.State == EntityState.Added && e.Entity.PaymentId == payment.Id)
            .Select(e => (DateTimeOffset?)e.Entity.OccurredUtc)
            .Max()
            ?? await db.PaymentEvents.AsNoTracking().Where(e => e.PaymentId == payment.Id)
                .MaxAsync(e => (DateTimeOffset?)e.OccurredUtc, cancellationToken);
        var at = TruncateToMicroseconds(now);
        if (previous is { } last && at <= last)
        {
            at = last.AddTicks(TimeSpan.TicksPerMicrosecond);
        }

        db.PaymentEvents.Add(new PaymentEvent
        {
            PaymentId = payment.Id,
            OrderId = payment.OrderId,
            OccurredUtc = at,
            Type = type,
            Amount = amount,
            Detail = detail is { Length: > 500 } ? detail[..500] : detail,
        });
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
}
