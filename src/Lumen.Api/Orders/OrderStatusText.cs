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
    /// <param name="refundPending">
    /// A refund of the order is recorded but not paid back yet (the refund job sends it within seconds; a failed one is
    /// retried): a refunded order says so until the money is back.
    /// </param>
    /// <param name="awaitingOneTimeCode">
    /// The current payment waits for the customer's one-time code (RequiresAction, code not expired): nothing is being
    /// confirmed until they enter it, so the order says what it waits for.
    /// </param>
    public static string ForOrder(OrderStatus status, bool refundPending = false, bool awaitingOneTimeCode = false) => status switch
    {
        OrderStatus.AwaitingPayment => "Awaiting payment",
        OrderStatus.PaymentProcessing => awaitingOneTimeCode ? "Waiting for your one-time code" : "Confirming your payment",
        OrderStatus.Paid => "Processing",
        OrderStatus.Completed => "Completed",
        OrderStatus.PartiallyRefunded => refundPending ? "Partial refund pending" : "Partially refunded",
        OrderStatus.Refunded => refundPending ? "Refund pending" : "Refunded",
        OrderStatus.Expired => "Expired",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <param name="source">The line's seller (a <see cref="ProductSources"/> value): own lines say "Confirmed", partner lines name the shop.</param>
    /// <param name="refunded">What the customer gets back for this line: its subtotal and tax, plus the seller's shipping fee when it is refunded with it.</param>
    /// <param name="refundPaid">The refund has been paid back (Succeeded); until then the line says "refund pending".</param>
    public static string ForLine(OrderLineStatus line, OrderStatus order, string source, string sellerName, decimal refunded, bool refundPaid) => line switch
    {
        OrderLineStatus.Pending => order == OrderStatus.Expired ? "Expired" : "Awaiting payment",
        OrderLineStatus.AwaitingPartner => $"Awaiting {sellerName}",
        OrderLineStatus.NeedsAttention => "Delayed",
        OrderLineStatus.Confirmed => source == ProductSources.Lumen ? "Confirmed" : $"Confirmed by {sellerName}",
        OrderLineStatus.Rejected when refunded <= 0 => "Not supplied",
        OrderLineStatus.Rejected => refundPaid ? $"Not supplied, refunded ${Money(refunded)}" : "Not supplied, refund pending",
        _ => throw new ArgumentOutOfRangeException(nameof(line), line, null),
    };

    /// <summary>"40.03", "1,355.17": invariant, two decimals (amounts are already rounded to cents).</summary>
    public static string Money(decimal amount) => amount.ToString("N2", CultureInfo.InvariantCulture);
}
