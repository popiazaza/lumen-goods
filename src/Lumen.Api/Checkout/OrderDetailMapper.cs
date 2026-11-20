using Lumen.Api.Catalog;
using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Orders;
using Lumen.Domain.Catalog;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Checkout;

/// <summary>
/// Builds <see cref="OrderDetailDto"/>, the answer to Place order (and the shape of <c>GET /orders/{number}</c>), with
/// the customer wording of BR-ORD-02. Reads the order with everything the page shows in one go.
/// </summary>
internal sealed class OrderDetailMapper(LumenDbContext db, IOptions<CatalogOptions> catalog, PaymentWindowPolicy window, TimeProvider time)
{
    public async Task<OrderDetailDto?> LoadAsync(Guid orderId, string mediaBaseUrl, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().AsSplitQuery()
            .Include(o => o.Lines).ThenInclude(l => l.Product)
            .Include(o => o.Shipments)
            .Include(o => o.Payments)
            .Include(o => o.Refunds).ThenInclude(r => r.Lines)
            .SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        return order is null ? null : Map(order, mediaBaseUrl);
    }

    public OrderDetailDto Map(Order order, string mediaBaseUrl)
    {
        var now = time.GetUtcNow();
        var shipments = order.Shipments
            .OrderBy(s => SellerOrder(s.Source))
            .Select(s => new OrderShipmentDto(
                s.Source,
                SellerName(s.Source),
                s.Method,
                s.Fee,
                s.FeeRefunded,
                [.. order.Lines.Where(l => l.Source == s.Source).OrderBy(l => l.LineNumber).Select(l => MapLine(l, mediaBaseUrl))]))
            .ToList();

        var payment = order.Payments.OrderByDescending(p => p.CreatedUtc).ThenByDescending(p => p.Id).FirstOrDefault();
        var canPay = order.Status == OrderStatus.AwaitingPayment
            && window.CanStartAttempt(order.FailedPaymentAttempts, order.CreatedUtc, now);
        var isUpdating = order.Status == OrderStatus.PaymentProcessing
            || (order.Status == OrderStatus.Paid && order.Lines.Any(l => !OrderTransitions.Line.IsFinal(l.Status)));

        return new OrderDetailDto(
            order.Number,
            order.Status,
            StatusText(order.Status),
            order.CreatedUtc.UtcDateTime,
            order.PaidUtc?.UtcDateTime,
            order.HoldExpiresUtc.UtcDateTime,
            shipments,
            new OrderTotalsDto(order.Subtotal, order.ShippingTotal, order.TaxTotal, order.Total, order.RefundedTotal, order.Total - order.RefundedTotal),
            ShippingAddressDto.From(order.ShipTo),
            order.ShippingMethodForOwnItems,
            payment is null
                ? null
                : new PaymentSummaryDto(payment.Id, payment.Method, payment.Status, payment.Amount, payment.CardBrand, payment.CardLast4,
                    payment.CompletedUtc?.UtcDateTime),
            [.. order.Refunds.OrderBy(r => r.CreatedUtc).Select(r =>
                new RefundDto(r.Id, r.Amount, r.Status, r.Reason, [.. r.Lines.Select(l => l.OrderLineId)], r.ShippingAmount,
                    r.CreatedUtc.UtcDateTime, r.CompletedUtc?.UtcDateTime))],
            canPay,
            isUpdating);
    }

    /// <summary>What the customer sees for the order's status (Paid reads "Processing": partners may still be confirming).</summary>
    public static string StatusText(OrderStatus status) => status switch
    {
        OrderStatus.AwaitingPayment => "Awaiting payment",
        OrderStatus.PaymentProcessing => "Confirming your payment",
        OrderStatus.Paid => "Processing",
        OrderStatus.Completed => "Completed",
        OrderStatus.PartiallyRefunded => "Partially refunded",
        OrderStatus.Refunded => "Refunded",
        OrderStatus.Expired => "Expired",
        _ => status.ToString(),
    };

    private OrderLineDto MapLine(OrderLine line, string mediaBaseUrl)
    {
        string? imageUrl = null;
        if (line.PrimaryImageSourceUrl is { } source && line.Product is { } product)
        {
            // The media endpoint serves the product's current images by index; only link when it is still the same image.
            var index = product.Images.FindIndex(i => i.SourceUrl == source);
            imageUrl = index >= 0 ? ProductMapping.ImageUrl(mediaBaseUrl, product.Id, index, source) : null;
        }

        return new OrderLineDto(line.Id, line.LineNumber, line.ProductId, line.Name, imageUrl, line.UnitPriceUsd, line.Quantity,
            line.LineSubtotal, line.Tax, line.Status, LineStatusText(line), line.RefundedAmount);
    }

    /// <summary>BR-ORD-02 wording with the seller's name.</summary>
    private string LineStatusText(OrderLine line)
    {
        var own = line.Source == ProductSources.Lumen;
        return line.Status switch
        {
            OrderLineStatus.Pending => "Awaiting payment",
            OrderLineStatus.AwaitingPartner => $"Awaiting {SellerName(line.Source)}",
            OrderLineStatus.NeedsAttention => "Delayed",
            OrderLineStatus.Confirmed => own ? "Confirmed" : $"Confirmed by {SellerName(line.Source)}",
            OrderLineStatus.Rejected => $"Not supplied, refunded {CheckoutCart.Money(line.RefundedAmount)}",
            _ => line.Status.ToString(),
        };
    }

    private string SellerName(string source) => catalog.Value.Sellers.FirstOrDefault(s => s.Source == source)?.Name ?? source;

    private static int SellerOrder(string source)
    {
        var index = ProductSources.All.ToList().IndexOf(source);
        return index < 0 ? int.MaxValue : index;
    }
}
