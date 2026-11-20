using System.Linq.Expressions;
using Lumen.Api.Catalog;
using Lumen.Api.Checkout;
using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Payments;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace Lumen.Api.Orders;

/// <summary>
/// Reads one order as the order pages show it: <see cref="OrderDetailDto"/> for the customer (and the answer to Place
/// order), <see cref="AdminOrderDto"/> for the admin. Read-only, no tracking. Lines are grouped by seller under their
/// shipment, each with its customer wording (<see cref="OrderStatusText"/>, BR-ORD-02) and refund (BR-ORD-03).
/// </summary>
internal sealed class OrderDetailReader(LumenDbContext db, SellerDirectory sellers, PaymentWindowPolicy paymentWindow, TimeProvider clock)
{
    /// <summary>The customer's own order; null when there is none with that number for this customer (shown as 404).</summary>
    public Task<OrderDetailDto?> ForCustomerAsync(string number, Guid customerId, string mediaBaseUrl, CancellationToken cancellationToken) =>
        DetailAsync(o => o.Number == number && o.CustomerId == customerId, mediaBaseUrl, cancellationToken);

    /// <summary>Any order by id, e.g. for the Place order answer right after the order was created.</summary>
    public Task<OrderDetailDto?> ByIdAsync(Guid orderId, string mediaBaseUrl, CancellationToken cancellationToken) =>
        DetailAsync(o => o.Id == orderId, mediaBaseUrl, cancellationToken);

    public async Task<AdminOrderDto?> ForAdminAsync(string number, string mediaBaseUrl, CancellationToken cancellationToken)
    {
        var order = await LoadAsync(o => o.Number == number, cancellationToken);
        if (order is null)
        {
            return null;
        }

        var detail = await ToDetailAsync(order, mediaBaseUrl, cancellationToken);
        var email = await db.Users.AsNoTracking().Where(u => u.Id == order.CustomerId).Select(u => u.Email).SingleOrDefaultAsync(cancellationToken);
        var events = await db.PaymentEvents.AsNoTracking()
            .Where(e => e.OrderId == order.Id)
            .OrderBy(e => e.OccurredUtc).ThenBy(e => e.Id)
            .Select(e => new { e.OccurredUtc, e.PaymentId, e.Type, e.Amount, e.Detail })
            .ToListAsync(cancellationToken);

        var partnerOrders = order.Lines
            .Where(l => l.PartnerOrder is not null)
            .OrderBy(l => l.LineNumber)
            .Select(l => ToDto(l, l.PartnerOrder!))
            .ToList();

        return new AdminOrderDto(
            detail,
            email ?? "",
            order.FailedPaymentAttempts,
            [.. events.Select(e => new PaymentEventDto(e.OccurredUtc.UtcDateTime, e.PaymentId, e.Type, e.Amount, e.Detail))],
            partnerOrders);
    }

    private async Task<OrderDetailDto?> DetailAsync(Expression<Func<Order, bool>> filter, string mediaBaseUrl, CancellationToken cancellationToken)
    {
        var order = await LoadAsync(filter, cancellationToken);
        return order is null ? null : await ToDetailAsync(order, mediaBaseUrl, cancellationToken);
    }

    private Task<Order?> LoadAsync(Expression<Func<Order, bool>> filter, CancellationToken cancellationToken) =>
        db.Orders.AsNoTracking()
            .Include(o => o.Lines).ThenInclude(l => l.PartnerOrder)
            .Include(o => o.Shipments)
            .Include(o => o.Payments)
            .Include(o => o.Refunds).ThenInclude(r => r.Lines)
            .AsSplitQuery()
            .SingleOrDefaultAsync(filter, cancellationToken);

    private async Task<OrderDetailDto> ToDetailAsync(Order order, string mediaBaseUrl, CancellationToken cancellationToken)
    {
        var productIds = order.Lines.Select(l => l.ProductId).Distinct().ToList();
        var images = await db.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Images })
            .ToDictionaryAsync(p => p.Id, p => p.Images, cancellationToken);
        return OrderDetailMapping.ToDetail(order, images, sellers, paymentWindow, clock.GetUtcNow(), mediaBaseUrl);
    }

    private PartnerOrderDto ToDto(OrderLine line, PartnerOrder partnerOrder) => new(
        partnerOrder.Id,
        line.Id,
        line.LineNumber,
        partnerOrder.Source,
        sellers.Name(partnerOrder.Source),
        partnerOrder.ExternalRef,
        partnerOrder.PartnerOrderId,
        partnerOrder.Status,
        partnerOrder.Reason,
        partnerOrder.Attempts,
        partnerOrder.NextAttemptUtc?.UtcDateTime,
        partnerOrder.LastError,
        partnerOrder.SubmittedUtc?.UtcDateTime,
        partnerOrder.AcceptedUtc?.UtcDateTime,
        partnerOrder.DecidedUtc?.UtcDateTime,
        NeedsAttention: line.Status == OrderLineStatus.NeedsAttention);
}

/// <summary>The order-to-DTO mapping, without the database (unit-tested on its own).</summary>
internal static class OrderDetailMapping
{
    public static OrderDetailDto ToDetail(
        Order order,
        IReadOnlyDictionary<Guid, List<ProductImage>> productImages,
        SellerDirectory sellers,
        PaymentWindowPolicy paymentWindow,
        DateTimeOffset now,
        string mediaBaseUrl)
    {
        var refundedPerLine = RefundedPerLine(order);
        var sources = order.Shipments.Select(s => s.Source).Concat(order.Lines.Select(l => l.Source)).Distinct(StringComparer.Ordinal)
            .OrderBy(sellers.Rank).ThenBy(s => s, StringComparer.Ordinal);

        var shipments = new List<OrderShipmentDto>();
        foreach (var source in sources)
        {
            var shipment = order.Shipments.FirstOrDefault(s => s.Source == source);
            var sellerName = sellers.Name(source);
            var lines = order.Lines
                .Where(l => l.Source == source)
                .OrderBy(l => l.LineNumber)
                .Select(l => new OrderLineDto(
                    l.Id,
                    l.LineNumber,
                    l.ProductId,
                    l.Name,
                    ImageUrl(l, productImages, mediaBaseUrl),
                    l.UnitPriceUsd,
                    l.Quantity,
                    l.LineSubtotal,
                    l.Tax,
                    l.Status,
                    OrderStatusText.ForLine(l.Status, order.Status, l.Source, sellerName, refundedPerLine.GetValueOrDefault(l.Id, l.RefundedAmount)),
                    l.RefundedAmount))
                .ToList();
            shipments.Add(new OrderShipmentDto(source, sellerName, shipment?.Method ?? default, shipment?.Fee ?? 0m, shipment?.FeeRefunded ?? false, lines));
        }

        var paid = order.PaidUtc is not null
                   || order.Status is OrderStatus.Paid or OrderStatus.Completed or OrderStatus.PartiallyRefunded or OrderStatus.Refunded;
        var payment = order.Payments.OrderByDescending(p => p.CreatedUtc).ThenByDescending(p => p.AttemptNumber).FirstOrDefault();

        return new OrderDetailDto(
            order.Number,
            order.Status,
            OrderStatusText.ForOrder(order.Status),
            order.CreatedUtc.UtcDateTime,
            order.PaidUtc?.UtcDateTime,
            order.HoldExpiresUtc.UtcDateTime,
            shipments,
            new OrderTotalsDto(
                order.Subtotal,
                order.ShippingTotal,
                order.TaxTotal,
                order.Total,
                order.RefundedTotal,
                NetPaid: paid ? order.Total - order.RefundedTotal : 0m),
            ShippingAddressDto.From(order.ShipTo),
            order.ShippingMethodForOwnItems,
            payment is null
                ? null
                : new PaymentSummaryDto(payment.Id, payment.Method, payment.Status, payment.Amount, payment.CardBrand, payment.CardLast4,
                    payment.CompletedUtc?.UtcDateTime),
            [.. order.Refunds
                .OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id)
                .Select(r => new RefundDto(r.Id, r.Amount, r.Status, r.Reason, [.. r.Lines.Select(l => l.OrderLineId)], r.ShippingAmount,
                    r.CreatedUtc.UtcDateTime, r.CompletedUtc?.UtcDateTime))],
            CanPay: order.Status == OrderStatus.AwaitingPayment && paymentWindow.CanStartAttempt(order.FailedPaymentAttempts, order.CreatedUtc, now),
            IsUpdating: order.Status == OrderStatus.PaymentProcessing || order.Lines.Any(l => l.Status == OrderLineStatus.AwaitingPartner));
    }

    /// <summary>
    /// What the customer got back per rejected line, for "Not supplied, refunded $X": the line's subtotal and tax (its
    /// refund line), plus the seller's shipping fee when the same refund paid it back (BR-ORD-03), counted on that
    /// refund's last line.
    /// </summary>
    public static Dictionary<Guid, decimal> RefundedPerLine(Order order)
    {
        var result = order.Lines.Where(l => l.RefundedAmount > 0).ToDictionary(l => l.Id, l => l.RefundedAmount);
        foreach (var refund in order.Refunds)
        {
            foreach (var part in refund.Lines)
            {
                result[part.OrderLineId] = part.Amount;
            }

            var last = order.Lines.Where(l => refund.Lines.Any(p => p.OrderLineId == l.Id)).MaxBy(l => l.LineNumber);
            if (last is not null && refund.ShippingAmount > 0)
            {
                result[last.Id] += refund.ShippingAmount;
            }
        }

        return result;
    }

    /// <summary>
    /// The Lumen media URL of the image the line was ordered with (never the partner's URL): its position among the
    /// product's current images, or the current primary image if it has changed since. Null when the line had none.
    /// </summary>
    private static string? ImageUrl(OrderLine line, IReadOnlyDictionary<Guid, List<ProductImage>> productImages, string mediaBaseUrl)
    {
        if (line.PrimaryImageSourceUrl is not { } ordered || !productImages.TryGetValue(line.ProductId, out var images) || images.Count == 0)
        {
            return null;
        }

        var index = images.FindIndex(i => i.SourceUrl == ordered);
        if (index < 0)
        {
            index = Math.Max(0, images.FindIndex(i => i.IsPrimary));
        }

        return ProductMapping.ImageUrl(mediaBaseUrl, line.ProductId, index, images[index].SourceUrl);
    }
}
