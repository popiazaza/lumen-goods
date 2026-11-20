using Lumen.Api.Orders;
using Lumen.Api.Payments;
using Lumen.Domain.Payments;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Lumen.IntegrationTests.Orders;

/// <summary>
/// BR-ORD-03 on real PostgreSQL: whatever the code above does (a rejection seen twice, a job retried), a line and a
/// shipment fee are refunded at most once, a refund is exactly its parts, and it only touches its own order.
/// The test order is one line of 2 × $18.50 (tax $2.96, so $39.96 to refund) and a $4.99 shipment.
/// </summary>
public sealed class RefundPersistenceTests(LumenApiFactory factory)
{
    private const decimal LineAmount = 39.96m;
    private const decimal Fee = 4.99m;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Order Order, Payment Payment)> PaidOrderAsync()
    {
        var order = await factory.NewSavedOrderAsync();
        var payment = OrderingTestData.NewPayment(order.Id, PaymentStatus.Succeeded);
        await factory.SaveAsync(payment);
        return (order, payment);
    }

    private static Refund RefundOf(Order order, Payment payment, decimal amount, OrderLine? line = null, OrderShipment? shipment = null)
    {
        var refund = new Refund
        {
            OrderId = order.Id, PaymentId = payment.Id, Amount = amount, Reason = "Not supplied by Harbor Supply Co.",
            ShipmentId = shipment?.Id, ShippingAmount = shipment?.Fee ?? 0m, CreatedUtc = OrderingTestData.Now,
        };
        if (line is not null)
        {
            refund.Lines.Add(new RefundLine { OrderId = order.Id, OrderLineId = line.Id, Amount = LineAmount });
        }

        return refund;
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_a_refund_is_exactly_its_lines_plus_its_shipping_part()
    {
        var (order, payment) = await PaidOrderAsync();

        (await factory.SaveRejectedAsync(RefundOf(order, payment, LineAmount + 0.01m, order.Lines[0])))
            .ShouldBeCheckViolation(RefundConfiguration.AmountIsSumOfParts);

        var whole = RefundOf(order, payment, LineAmount + Fee, order.Lines[0], order.Shipments[0]);
        await factory.SaveAsync(whole);

        await using var db = factory.NewContext();
        var loaded = await db.Refunds.AsNoTracking().Include(r => r.Lines).SingleAsync(r => r.Id == whole.Id, Ct);
        loaded.Lines.Should().ContainSingle().Which.OrderLineId.Should().Be(order.Lines[0].Id);
        loaded.ShippingAmount.Should().Be(Fee);
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_a_refund_is_never_zero_and_refunds_shipping_only_with_its_shipment()
    {
        var (order, payment) = await PaidOrderAsync();

        (await factory.SaveRejectedAsync(RefundOf(order, payment, 0m))).ShouldBeCheckViolation("CK_Refunds_AmountPositive");

        var shippingWithoutShipment = RefundOf(order, payment, LineAmount + Fee, order.Lines[0]);
        shippingWithoutShipment.ShippingAmount = Fee;
        (await factory.SaveRejectedAsync(shippingWithoutShipment)).ShouldBeCheckViolation("CK_Refunds_ShippingAmountWithShipment");
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_a_line_is_refunded_at_most_once()
    {
        var (order, payment) = await PaidOrderAsync();
        await factory.SaveAsync(RefundOf(order, payment, LineAmount, order.Lines[0]));

        (await factory.SaveRejectedAsync(RefundOf(order, payment, LineAmount, order.Lines[0])))
            .ShouldBeUniqueViolation("IX_RefundLines_OrderLineId");
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_a_shipment_fee_is_refunded_at_most_once()
    {
        var (order, payment) = await PaidOrderAsync();
        await factory.SaveAsync(RefundOf(order, payment, Fee, shipment: order.Shipments[0]));

        (await factory.SaveRejectedAsync(RefundOf(order, payment, Fee, shipment: order.Shipments[0])))
            .ShouldBeUniqueViolation("IX_Refunds_ShipmentId_Unique");
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_a_refund_touches_only_its_own_orders_lines_shipments_and_payment()
    {
        var (order, payment) = await PaidOrderAsync();
        var (other, otherPayment) = await PaidOrderAsync();

        (await factory.SaveRejectedAsync(RefundOf(order, payment, LineAmount, other.Lines[0])))
            .ShouldBeForeignKeyViolation("FK_RefundLines_OrderLines_OrderLineId_OrderId");
        (await factory.SaveRejectedAsync(RefundOf(order, payment, Fee, shipment: other.Shipments[0])))
            .ShouldBeForeignKeyViolation("FK_Refunds_OrderShipments_ShipmentId_OrderId");
        (await factory.SaveRejectedAsync(RefundOf(order, otherPayment, LineAmount, order.Lines[0])))
            .ShouldBeForeignKeyViolation("FK_Refunds_Payments_PaymentId_OrderId");
    }
}
