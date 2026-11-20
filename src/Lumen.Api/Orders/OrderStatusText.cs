using System.Globalization;
using Lumen.Domain.Catalog;
using Lumen.Domain.Orders;

namespace Lumen.Api.Orders;

/// <summary>
/// BR-ORD-02: what the customer reads for an order and for each line, with the seller's name. One place, so the order
/// list, the order page, the confirmation page and the admin pages say the same thing.
/// </summary>
public static class OrderStatusText
{
    public static string ForOrder(OrderStatus status) => status switch
    {
        OrderStatus.AwaitingPayment => "Awaiting payment",
        OrderStatus.PaymentProcessing => "Confirming your payment",
        OrderStatus.Paid => "Processing",
        OrderStatus.Completed => "Completed",
        OrderStatus.PartiallyRefunded => "Partially refunded",
        OrderStatus.Refunded => "Refunded",
        OrderStatus.Expired => "Expired",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <param name="source">The line's seller (a <see cref="ProductSources"/> value): own lines say "Confirmed", partner lines name the shop.</param>
    /// <param name="refunded">What the customer got back for this line: its subtotal and tax, plus the seller's shipping fee when it was refunded with it.</param>
    public static string ForLine(OrderLineStatus line, OrderStatus order, string source, string sellerName, decimal refunded) => line switch
    {
        OrderLineStatus.Pending => order == OrderStatus.Expired ? "Expired" : "Awaiting payment",
        OrderLineStatus.AwaitingPartner => $"Awaiting {sellerName}",
        OrderLineStatus.NeedsAttention => "Delayed",
        OrderLineStatus.Confirmed => source == ProductSources.Lumen ? "Confirmed" : $"Confirmed by {sellerName}",
        OrderLineStatus.Rejected => $"Not supplied, refunded ${Money(refunded)}",
        _ => throw new ArgumentOutOfRangeException(nameof(line), line, null),
    };

    /// <summary>"40.03", "1,355.17": invariant, two decimals (amounts are already rounded to cents).</summary>
    public static string Money(decimal amount) => amount.ToString("N2", CultureInfo.InvariantCulture);
}
