using Lumen.Api.Infrastructure.Data;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace Lumen.Api.Orders;

/// <summary>
/// BR-ORD-03: a paid line that will not be supplied is rejected and refunded in one step, in the caller's transaction,
/// so a rejected line can never be left without its refund (or refunded twice). Used by the partner order job (partner
/// rejected it, or it was given up, BR-ORD-04) and by an admin resolving a delayed line.
/// This only records the refund (Pending); the refund job sends it to the original payment method outside any
/// transaction, with the refund id as idempotency key.
/// </summary>
internal static class LineRejection
{
    public const int MaxReasonLength = 500;

    /// <summary>
    /// Rejects <paramref name="line"/> and records what is owed for it (<see cref="RefundCalculator"/>): its subtotal
    /// and tax, plus its seller's shipping fee when this was that seller's last line still going ahead. Updates the
    /// line's and the order's refunded amounts and the shipment's fee flag, then the order status (BR-ORD-02).
    /// </summary>
    /// <param name="order">Tracked, with its lines and shipments loaded; the caller holds the order row lock.</param>
    /// <returns>The refund to add (Pending), or null when nothing is owed (the line was already rejected, or was free).</returns>
    /// <exception cref="InvalidOperationException">The line cannot be rejected from its status (e.g. it is Confirmed).</exception>
    public static Refund? Reject(Order order, OrderLine line, Guid paymentId, string reason, DateTimeOffset now)
    {
        var snapshot = new PaidOrderSnapshot(
            [.. order.Lines.Select(l => new PaidOrderLine(l.Id, l.Source, l.LineSubtotal, l.Tax, l.Status))],
            [.. order.Shipments.Select(s => new PaidShipment(s.Source, s.Fee, s.FeeRefunded))],
            AmountCharged: order.Total,
            AmountRefunded: order.RefundedTotal);

        // Computed from the statuses before this rejection: an already Rejected line refunds nothing.
        var breakdown = RefundCalculator.Calculate(snapshot, [line.Id]);
        line.Status = OrderTransitions.Line.MoveIfChanged(line.Status, OrderLineStatus.Rejected);

        DeriveOrderStatus(order);
        if (breakdown.Total <= 0)
        {
            return null;
        }

        // One line is rejected, so at most its own seller's fee is refunded: one refund never covers two shipments.
        var shipping = breakdown.Shipping.SingleOrDefault();
        var shipment = shipping is null ? null : order.Shipments.Single(s => s.Source == shipping.Source);
        var refund = new Refund
        {
            OrderId = order.Id,
            PaymentId = paymentId,
            Amount = breakdown.Total,
            ShipmentId = shipment?.Id,
            ShippingAmount = shipping?.Fee ?? 0m,
            Status = OrderTransitions.Refund.Initial,
            Reason = Truncate(reason),
            CreatedUtc = now,

            // Due at once: the refund job sends it on its next run.
            NextAttemptUtc = now,
        };

        // The unique RefundLine.OrderLineId and Refund.ShipmentId are what make a second refund impossible; the line and
        // shipment fields below only mirror them for display, in the same transaction.
        foreach (var refunded in breakdown.Lines)
        {
            refund.Lines.Add(new RefundLine { OrderId = order.Id, OrderLineId = refunded.LineId, Amount = refunded.Amount });
            order.Lines.Single(l => l.Id == refunded.LineId).RefundedAmount = refunded.Amount;
        }

        if (shipment is not null)
        {
            shipment.FeeRefunded = true;
        }

        order.RefundedTotal += breakdown.Total;
        return refund;
    }

    /// <summary>BR-ORD-02: after any line change the order status follows its lines (an unchanged status is a no-op).</summary>
    public static void DeriveOrderStatus(Order order) =>
        order.Status = OrderTransitions.Order.MoveIfChanged(
            order.Status, OrderStatusDerivation.Derive(order.Status, [.. order.Lines.Select(l => l.Status)]));

    /// <summary>The payment a refund goes back to: the order's successful payment (card or wallet).</summary>
    /// <exception cref="InvalidOperationException">A paid order without a successful payment: a bug, never refunded blindly.</exception>
    public static async Task<Guid> SucceededPaymentIdAsync(LumenDbContext db, Guid orderId, CancellationToken cancellationToken) =>
        await db.Payments.AsNoTracking()
            .Where(p => p.OrderId == orderId && p.Status == PaymentStatus.Succeeded)
            .OrderByDescending(p => p.CompletedUtc)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw new InvalidOperationException($"Order {orderId} is paid but has no succeeded payment to refund to.");

    public static string Truncate(string text, int max = MaxReasonLength) => text.Length <= max ? text : text[..(max - 3)] + "...";
}
