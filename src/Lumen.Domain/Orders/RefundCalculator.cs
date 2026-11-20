namespace Lumen.Domain.Orders;

/// <summary>What the refund calculator needs to know about a paid order, read in the transaction that records the refund.</summary>
/// <param name="AmountCharged">What the customer paid for the order (the order's grand total).</param>
/// <param name="AmountRefunded">
/// The sum of every refund already recorded for the order, whatever its <see cref="RefundStatus"/>: a Pending or
/// Failed refund will still be paid out (a failed one is retried, never replaced).
/// </param>
public sealed record PaidOrderSnapshot(
    IReadOnlyList<PaidOrderLine> Lines,
    IReadOnlyList<PaidShipment> Shipments,
    decimal AmountCharged,
    decimal AmountRefunded);

/// <param name="Source">The seller that ships the line, i.e. which <see cref="PaidShipment"/> it belongs to.</param>
public sealed record PaidOrderLine(Guid LineId, string Source, decimal Subtotal, decimal Tax, OrderLineStatus Status);

public sealed record PaidShipment(string Source, decimal Fee, bool FeeRefunded);

public sealed record LineRefund(Guid LineId, string Source, decimal Subtotal, decimal Tax)
{
    public decimal Amount => Subtotal + Tax;
}

public sealed record ShippingRefund(string Source, decimal Fee);

/// <summary>The refund to pay out. <see cref="Total"/> is what to send back; it is 0 when nothing is owed.</summary>
/// <param name="Capped">True when the items add up to more than is left to refund and <see cref="Total"/> was cut to that.</param>
public sealed record RefundBreakdown(IReadOnlyList<LineRefund> Lines, IReadOnlyList<ShippingRefund> Shipping, decimal Total, bool Capped);

/// <summary>
/// BR-ORD-03: a line a partner cannot supply is refunded: its subtotal and its own tax, plus the seller's shipping fee
/// once every line of that shipment has been rejected (counting earlier rejections) and the fee was not refunded yet.
/// - Idempotent: a line already Rejected was already refunded and refunds nothing, however often the rejection is seen.
/// - Never more than was paid: the total is capped at amount charged − amount already refunded.
/// - Nothing is ever charged: the result is never negative.
/// </summary>
public static class RefundCalculator
{
    /// <exception cref="ArgumentException">A line id is not on the order, or a line's seller has no shipment.</exception>
    /// <exception cref="InvalidOperationException">A line cannot be rejected from its status (e.g. it is already Confirmed).</exception>
    public static RefundBreakdown Calculate(PaidOrderSnapshot order, IReadOnlyCollection<Guid> rejectedLineIds)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(rejectedLineIds);

        var linesById = order.Lines.ToDictionary(l => l.LineId);
        var newlyRejected = new List<PaidOrderLine>();
        foreach (var id in rejectedLineIds.Distinct())
        {
            if (!linesById.TryGetValue(id, out var line))
            {
                throw new ArgumentException($"Line {id} is not on this order.", nameof(rejectedLineIds));
            }

            if (line.Status == OrderLineStatus.Rejected)
            {
                continue; // refunded when it was first rejected
            }

            OrderTransitions.Line.Move(line.Status, OrderLineStatus.Rejected);
            newlyRejected.Add(line);
        }

        var lineRefunds = newlyRejected.Select(l => new LineRefund(l.LineId, l.Source, l.Subtotal, l.Tax)).ToList();

        var rejectedNow = newlyRejected.Select(l => l.LineId).ToHashSet();
        var shipmentsBySource = order.Shipments.ToDictionary(s => s.Source);
        var shippingRefunds = new List<ShippingRefund>();
        foreach (var source in newlyRejected.Select(l => l.Source).Distinct())
        {
            if (!shipmentsBySource.TryGetValue(source, out var shipment))
            {
                throw new ArgumentException($"The order has no '{source}' shipment for its '{source}' lines.", nameof(order));
            }

            var wholeShipmentRejected = order.Lines
                .Where(l => l.Source == source)
                .All(l => l.Status == OrderLineStatus.Rejected || rejectedNow.Contains(l.LineId));
            if (wholeShipmentRejected && !shipment.FeeRefunded && shipment.Fee > 0)
            {
                shippingRefunds.Add(new ShippingRefund(source, shipment.Fee));
            }
        }

        var owed = lineRefunds.Sum(r => r.Amount) + shippingRefunds.Sum(r => r.Fee);
        var leftToRefund = Math.Max(0m, order.AmountCharged - order.AmountRefunded);
        var total = Math.Min(owed, leftToRefund);
        return new RefundBreakdown(lineRefunds, shippingRefunds, total, Capped: total < owed);
    }
}
