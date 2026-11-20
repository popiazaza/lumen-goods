using Lumen.Api.Orders;
using Lumen.Api.Payments;
using Lumen.Domain.Catalog;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Lumen.Domain.Pricing;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Lumen.IntegrationTests.Orders;

/// <summary>
/// The order tables on real PostgreSQL: the snapshot round-trips, and every constraint that keeps money and
/// idempotency right holds even if the code above it gets something wrong.
/// </summary>
public sealed class OrderPersistenceTests(LumenApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_order_round_trips_with_its_address_lines_shipments_and_statuses_stored_as_names()
    {
        var order = await factory.NewSavedOrderAsync();

        await using var db = factory.NewContext();
        var loaded = await db.Orders.AsNoTracking()
            .Include(o => o.Lines).Include(o => o.Shipments)
            .SingleAsync(o => o.Id == order.Id, Ct);

        loaded.ShipTo.Should().Be(order.ShipTo);
        loaded.Status.Should().Be(OrderStatus.AwaitingPayment);
        loaded.Total.Should().Be(44.95m);
        loaded.Lines.Should().ContainSingle().Which.Status.Should().Be(OrderLineStatus.Pending);
        loaded.Shipments.Should().ContainSingle().Which.Method.Should().Be(ShippingMethod.Standard);

        var stored = await db.Database
            .SqlQuery<string>($"SELECT \"Status\" AS \"Value\" FROM \"Orders\" WHERE \"Id\" = {order.Id}")
            .SingleAsync(Ct);
        stored.Should().Be("AwaitingPayment");
    }

    [Fact]
    [Trait("BR", "BR-PAY-01")]
    public async Task BR_PAY_01_a_checkout_key_creates_one_order_only()
    {
        var first = await factory.NewSavedOrderAsync();
        var second = OrderingTestData.NewOrder(first.CustomerId, first.Lines[0].ProductId);
        second.CheckoutKey = first.CheckoutKey;

        (await factory.SaveRejectedAsync(second)).ShouldBeUniqueViolation("IX_Orders_CheckoutKey");
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_order_numbers_are_unique()
    {
        var first = await factory.NewSavedOrderAsync();
        var second = OrderingTestData.NewOrder(first.CustomerId, first.Lines[0].ProductId);
        second.Number = first.Number;

        (await factory.SaveRejectedAsync(second)).ShouldBeUniqueViolation("IX_Orders_Number");
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_refunds_never_exceed_the_amount_charged()
    {
        var order = await factory.NewSavedOrderAsync();

        await using var db = factory.NewContext();
        var refundAll = await db.Orders.Where(o => o.Id == order.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.RefundedTotal, o => o.Total), Ct);
        refundAll.Should().Be(1);

        await FluentActions.Awaiting(() => db.Orders.Where(o => o.Id == order.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.RefundedTotal, o => o.Total + 0.01m), Ct))
            .Should().ThrowAsync<Npgsql.PostgresException>()
            .Where(e => e.ConstraintName == "CK_Orders_RefundedNotAboveTotal");
    }

    [Fact]
    public async Task The_total_is_always_subtotal_plus_shipping_plus_tax_and_no_amount_is_negative()
    {
        var customerId = await factory.NewCustomerAsync();
        var product = await factory.NewProductAsync();

        var wrongTotal = OrderingTestData.NewOrder(customerId, product.Id);
        wrongTotal.Total = 44.94m;
        (await factory.SaveRejectedAsync(wrongTotal)).ShouldBeCheckViolation("CK_Orders_TotalIsSum");

        var negative = OrderingTestData.NewOrder(customerId, product.Id);
        negative.ShippingTotal = -4.99m;
        negative.Total = 37.00m + 2.96m - 4.99m;
        (await factory.SaveRejectedAsync(negative)).ShouldBeCheckViolation("CK_Orders_AmountsNotNegative");
    }

    [Fact]
    [Trait("BR", "BR-MONEY-01")]
    public async Task BR_MONEY_01_a_line_subtotal_is_unit_price_times_quantity()
    {
        var customerId = await factory.NewCustomerAsync();
        var product = await factory.NewProductAsync();
        var order = OrderingTestData.NewOrder(customerId, product.Id);
        order.Lines[0].LineSubtotal = 37.01m;
        order.Subtotal = 37.01m;
        order.Total = 44.96m;

        (await factory.SaveRejectedAsync(order)).ShouldBeCheckViolation("CK_OrderLines_SubtotalIsUnitTimesQuantity");
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_a_line_refunds_at_most_its_subtotal_and_tax()
    {
        var customerId = await factory.NewCustomerAsync();
        var product = await factory.NewProductAsync();
        var order = OrderingTestData.NewOrder(customerId, product.Id);
        order.Lines[0].RefundedAmount = 37.00m + 2.96m + 0.01m;

        (await factory.SaveRejectedAsync(order)).ShouldBeCheckViolation("CK_OrderLines_RefundedNotAboveLineTotal");
    }

    [Fact]
    public async Task Line_numbers_are_unique_within_an_order()
    {
        var order = await factory.NewSavedOrderAsync();
        var line = new OrderLine
        {
            OrderId = order.Id, LineNumber = 1, ProductId = order.Lines[0].ProductId, Source = ProductSources.Lumen,
            SourceProductId = "LG-AC-001", Name = "Second mug", UnitPriceUsd = 1.00m, Quantity = 1, LineSubtotal = 1.00m,
        };

        (await factory.SaveRejectedAsync(line)).ShouldBeUniqueViolation("IX_OrderLines_OrderId_LineNumber");
    }

    [Fact]
    [Trait("BR", "BR-SHIP-01")]
    public async Task BR_SHIP_01_an_order_has_one_shipment_per_seller()
    {
        var order = await factory.NewSavedOrderAsync();
        var second = new OrderShipment { OrderId = order.Id, Source = ProductSources.Lumen, Method = ShippingMethod.Express, Fee = 12.99m };

        (await factory.SaveRejectedAsync(second)).ShouldBeUniqueViolation("IX_OrderShipments_OrderId_Source");
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_one_partner_order_per_line_and_each_external_reference_once()
    {
        var first = await factory.NewSavedOrderAsync();
        var second = await factory.NewSavedOrderAsync();
        PartnerOrder For(OrderLine line, string externalRef) => new()
        {
            OrderLineId = line.Id, Source = ProductSources.Harbor, ExternalRef = externalRef, CreatedUtc = OrderingTestData.Now,
            NextAttemptUtc = OrderingTestData.Now,
        };

        var reference = $"{first.Number}-1";
        await factory.SaveAsync(For(first.Lines[0], reference));

        (await factory.SaveRejectedAsync(For(second.Lines[0], reference))).ShouldBeUniqueViolation("IX_PartnerOrders_ExternalRef");
        (await factory.SaveRejectedAsync(For(first.Lines[0], $"{first.Number}-9"))).ShouldBeUniqueViolation("IX_PartnerOrders_OrderLineId");

        await using var db = factory.NewContext();
        var loaded = await db.PartnerOrders.AsNoTracking().SingleAsync(p => p.ExternalRef == reference, Ct);
        loaded.Status.Should().Be(PartnerOrderStatus.Pending);
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_a_refund_keeps_its_line_ids_as_a_jsonb_array_and_is_never_zero()
    {
        var order = await factory.NewSavedOrderAsync();
        var payment = OrderingTestData.NewPayment(order.Id, PaymentStatus.Succeeded);
        var lineIds = new List<Guid> { order.Lines[0].Id, Guid.NewGuid() };
        var refund = new Refund
        {
            OrderId = order.Id, PaymentId = payment.Id, Amount = 39.96m, Reason = "Not supplied", LineIds = lineIds,
            CreatedUtc = OrderingTestData.Now,
        };
        await factory.SaveAsync(payment, refund);

        await using (var db = factory.NewContext())
        {
            (await db.Refunds.AsNoTracking().SingleAsync(r => r.Id == refund.Id, Ct)).LineIds.Should().Equal(lineIds);
            var type = await db.Database
                .SqlQuery<string>($"SELECT jsonb_typeof(\"LineIds\") AS \"Value\" FROM \"Refunds\" WHERE \"Id\" = {refund.Id}")
                .SingleAsync(Ct);
            type.Should().Be("array");
        }

        var zero = new Refund { OrderId = order.Id, PaymentId = payment.Id, Amount = 0m, Reason = "Nothing", CreatedUtc = OrderingTestData.Now };
        (await factory.SaveRejectedAsync(zero)).ShouldBeCheckViolation("CK_Refunds_AmountPositive");
    }

    [Fact]
    public async Task The_admin_list_and_order_history_have_their_indexes()
    {
        await using var db = factory.NewContext();
        var indexes = await db.Database
            .SqlQuery<string>($"SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename = 'Orders'")
            .ToListAsync(Ct);

        indexes.Should().Contain(["IX_Orders_Status_CreatedUtc", "IX_Orders_CustomerId_CreatedUtc", "IX_Orders_Unpaid_HoldExpiresUtc"]);
    }
}
