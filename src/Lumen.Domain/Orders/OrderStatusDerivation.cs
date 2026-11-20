namespace Lumen.Domain.Orders;

/// <summary>
/// BR-ORD-02: once an order is paid, its status follows its lines, so the customer never sees an order status that
/// disagrees with the items in it.
/// </summary>
public static class OrderStatusDerivation
{
    /// <summary>
    /// The status the order should have now. Before payment (or once expired) the lines decide nothing and
    /// <paramref name="current"/> is returned. After payment: any line still open → Paid (shown as "Processing");
    /// otherwise all confirmed → Completed, all refunded → Refunded, a mix → PartiallyRefunded.
    /// Apply it with <c>order.Status = OrderTransitions.Order.MoveIfChanged(order.Status, derived)</c>: an unchanged
    /// status (the usual case) is a no-op.
    /// </summary>
    public static OrderStatus Derive(OrderStatus current, IReadOnlyCollection<OrderLineStatus> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (current is OrderStatus.AwaitingPayment or OrderStatus.PaymentProcessing or OrderStatus.Expired)
        {
            return current;
        }

        if (lines.Count == 0)
        {
            throw new ArgumentException("A paid order has at least one line.", nameof(lines));
        }

        if (lines.Any(line => !OrderTransitions.Line.IsFinal(line)))
        {
            return OrderStatus.Paid;
        }

        if (lines.All(line => line == OrderLineStatus.Confirmed))
        {
            return OrderStatus.Completed;
        }

        return lines.All(line => line == OrderLineStatus.Rejected) ? OrderStatus.Refunded : OrderStatus.PartiallyRefunded;
    }
}
