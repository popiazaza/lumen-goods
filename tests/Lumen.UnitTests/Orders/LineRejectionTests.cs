using Lumen.Api.Orders;
using Lumen.Domain.Catalog;
using Lumen.Domain.Orders;
using Lumen.Domain.Pricing;

namespace Lumen.UnitTests.Orders;

/// <summary>
/// BR-ORD-03: rejecting a paid line records exactly one refund of the line and its tax, plus its seller's shipping when
/// that seller's whole shipment failed, keeps the display fields in step, and moves the order status (BR-ORD-02).
/// </summary>
public sealed class LineRejectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 8, 30, 0, TimeSpan.Zero);
    private static readonly Guid PaymentId = Guid.NewGuid();

    internal static OrderLine Line(int number, string source, decimal unitPrice, int quantity, OrderLineStatus status)
    {
        var subtotal = unitPrice * quantity;
        return new OrderLine
        {
            LineNumber = number,
            ProductId = Guid.NewGuid(),
            Source = source,
            SourceProductId = $"{source}-{number}",
            Name = $"Item {number}",
            UnitPriceUsd = unitPrice,
            Quantity = quantity,
            LineSubtotal = subtotal,
            Tax = Math.Round(subtotal * 0.08m, 2, MidpointRounding.AwayFromZero),
            Status = status,
        };
    }

    /// <summary>A paid order with these lines and a Standard $4.99 shipment per seller, totals as charged.</summary>
    internal static Order PaidOrder(params OrderLine[] lines)
    {
        var order = new Order
        {
            Number = "LG-7K3M9QX2",
            ShipTo = new ShippingAddress("Ada Lovelace", "1200 NW Naito Pkwy", null, "Portland", "OR", "97209", "US"),
            Status = OrderStatus.Paid,
            Lines = [.. lines],
            Shipments = [.. lines.Select(l => l.Source).Distinct().Select(s => new OrderShipment { Source = s, Method = ShippingMethod.Standard, Fee = 4.99m })],
        };
        foreach (var line in lines)
        {
            line.OrderId = order.Id;
        }

        order.Subtotal = lines.Sum(l => l.LineSubtotal);
        order.TaxTotal = lines.Sum(l => l.Tax);
        order.ShippingTotal = order.Shipments.Sum(s => s.Fee);
        order.Total = order.Subtotal + order.TaxTotal + order.ShippingTotal;
        return order;
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public void BR_ORD_03_a_kestrel_line_not_supplied_refunds_line_tax_and_shipping_40_03_and_the_rest_goes_ahead()
    {
        var own = Line(1, ProductSources.Lumen, 18.50m, 1, OrderLineStatus.Confirmed);
        var kestrel = Line(2, ProductSources.Kestrel, 16.22m, 2, OrderLineStatus.AwaitingPartner);
        var order = PaidOrder(own, kestrel);

        var refund = LineRejection.Reject(order, kestrel, PaymentId, "Not supplied by Kestrel Outfitters (OUT_OF_STOCK:K-7731)", Now)!;

        refund.Amount.Should().Be(40.03m);
        refund.ShippingAmount.Should().Be(4.99m);
        refund.ShipmentId.Should().Be(order.Shipments.Single(s => s.Source == ProductSources.Kestrel).Id);
        refund.Lines.Should().ContainSingle().Which.Should().BeEquivalentTo(new { OrderId = order.Id, OrderLineId = kestrel.Id, Amount = 35.04m });
        refund.Status.Should().Be(RefundStatus.Pending);
        refund.PaymentId.Should().Be(PaymentId);
        refund.NextAttemptUtc.Should().Be(Now);

        kestrel.Status.Should().Be(OrderLineStatus.Rejected);
        kestrel.RefundedAmount.Should().Be(35.04m);
        own.Status.Should().Be(OrderLineStatus.Confirmed);
        order.Shipments.Single(s => s.Source == ProductSources.Kestrel).FeeRefunded.Should().BeTrue();
        order.Shipments.Single(s => s.Source == ProductSources.Lumen).FeeRefunded.Should().BeFalse();
        order.RefundedTotal.Should().Be(40.03m);
        order.Status.Should().Be(OrderStatus.PartiallyRefunded);
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public void BR_ORD_03_one_of_two_harbor_lines_refunds_only_that_line_and_its_tax_shipping_stays_charged()
    {
        var kept = Line(1, ProductSources.Harbor, 24.95m, 2, OrderLineStatus.Confirmed);
        var rejected = Line(2, ProductSources.Harbor, 15.00m, 1, OrderLineStatus.AwaitingPartner);
        var order = PaidOrder(kept, rejected);

        var refund = LineRejection.Reject(order, rejected, PaymentId, "Not supplied by Harbor Supply Co. (INVALID_ITEM)", Now)!;

        refund.Amount.Should().Be(16.20m);
        refund.ShipmentId.Should().BeNull();
        refund.ShippingAmount.Should().Be(0m);
        order.Shipments.Single().FeeRefunded.Should().BeFalse();
        order.Status.Should().Be(OrderStatus.PartiallyRefunded);
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public void BR_ORD_03_when_every_line_fails_the_refunds_add_up_to_the_amount_charged_and_never_more()
    {
        var harbor = Line(1, ProductSources.Harbor, 49.90m, 1, OrderLineStatus.AwaitingPartner);
        var kestrel = Line(2, ProductSources.Kestrel, 16.22m, 2, OrderLineStatus.AwaitingPartner);
        var order = PaidOrder(harbor, kestrel);

        var first = LineRejection.Reject(order, harbor, PaymentId, "Not supplied by Harbor Supply Co.", Now)!;
        var second = LineRejection.Reject(order, kestrel, PaymentId, "Not supplied by Kestrel Outfitters", Now)!;
        var again = LineRejection.Reject(order, kestrel, PaymentId, "Not supplied by Kestrel Outfitters", Now);

        (first.Amount + second.Amount).Should().Be(order.Total);
        again.Should().BeNull("a line already rejected was already refunded, however often the rejection is seen");
        order.RefundedTotal.Should().Be(order.Total);
        order.Status.Should().Be(OrderStatus.Refunded);
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public void BR_ORD_03_a_confirmed_line_cannot_be_rejected()
    {
        var confirmed = Line(1, ProductSources.Harbor, 10m, 1, OrderLineStatus.Confirmed);
        var order = PaidOrder(confirmed);

        FluentActions.Invoking(() => LineRejection.Reject(order, confirmed, PaymentId, "x", Now)).Should().Throw<InvalidOperationException>();
        order.RefundedTotal.Should().Be(0m);
    }

    [Fact]
    [Trait("BR", "BR-ORD-04")]
    public void BR_ORD_04_a_line_that_needs_attention_can_be_rejected_by_an_admin_and_is_refunded()
    {
        var delayed = Line(1, ProductSources.Kestrel, 16.22m, 2, OrderLineStatus.NeedsAttention);
        var order = PaidOrder(delayed);

        var refund = LineRejection.Reject(order, delayed, PaymentId, "Not supplied by Kestrel Outfitters (rejected by an admin)", Now)!;

        refund.Amount.Should().Be(order.Total);
        order.Status.Should().Be(OrderStatus.Refunded);
    }
}
