using System.Net;
using System.Text.Json;
using Lumen.Api.Catalog;
using Lumen.Api.Orders;
using Lumen.Domain.Catalog;
using Lumen.Domain.Orders;
using Lumen.IntegrationTests.Catalog;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static Lumen.IntegrationTests.Orders.OrderingTestData;

namespace Lumen.IntegrationTests.Orders;

/// <summary>
/// The customer's order history and order page over HTTP (BR-ORD-02, BR-ADM-01): only their own orders, newest first,
/// each line under its shop with a status the customer understands.
/// </summary>
public sealed class CustomerOrderApiTests(LumenApiFactory factory)
{
    [Fact]
    [Trait("BR", "BR-ADM-01")]
    public async Task BR_ADM_01_another_customers_order_is_404_like_one_that_does_not_exist_and_anonymous_is_401()
    {
        var (owner, ownerSession) = await factory.SignedInCustomerAsync();
        var (_, otherSession) = await factory.SignedInCustomerAsync();
        var order = await factory.OrderAsync(owner, OrderStatus.Paid, Now, LineSpec.Own(), LineSpec.Harbor(49.90m));

        (await ownerSession.GetAsync($"/api/v1/orders/{order.Number}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ownerSession.GetAsync($"/api/v1/orders/{order.Number.ToLowerInvariant()}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var foreign = await otherSession.GetAsync($"/api/v1/orders/{order.Number}");
        var missing = await otherSession.GetAsync($"/api/v1/orders/{new OrderNumberGenerator().Next()}");
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await foreign.Content.ReadAsStringAsync(Ct)).Should().Be(await missing.Content.ReadAsStringAsync(Ct), "a foreign order reveals nothing");
        (await otherSession.GetAsync("/api/v1/orders/not-an-order")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var anonymous = factory.CreateSession();
        (await anonymous.GetAsync($"/api/v1/orders/{order.Number}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/v1/orders")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("BR", "BR-ORD-02")]
    public async Task BR_ORD_02_the_history_lists_only_my_orders_newest_first_with_the_shops_they_came_from()
    {
        var (me, session) = await factory.SignedInCustomerAsync();
        var (someoneElse, _) = await factory.SignedInCustomerAsync();
        var oldest = await factory.OrderAsync(me, OrderStatus.Paid, Now.AddDays(-2), LineSpec.Own(18.50m, 2), LineSpec.Kestrel(16.22m));
        var middle = await factory.OrderAsync(me, OrderStatus.Expired, Now.AddDays(-1), LineSpec.Harbor(49.90m));
        var newest = await factory.OrderAsync(me, OrderStatus.AwaitingPayment, Now, LineSpec.Kestrel(16.22m), LineSpec.Harbor(10m), LineSpec.Own());
        await factory.OrderAsync(someoneElse, OrderStatus.Paid, Now.AddMinutes(5), LineSpec.Own());

        var first = await (await session.GetAsync("/api/v1/orders?pageSize=2")).ReadAsync<PagedResult<OrderSummaryDto>>();
        var second = await (await session.GetAsync("/api/v1/orders?pageSize=2&page=2")).ReadAsync<PagedResult<OrderSummaryDto>>();

        first.TotalCount.Should().Be(3);
        first.TotalPages.Should().Be(2);
        first.Items.Concat(second.Items).Select(o => o.Number).Should().Equal(newest.Number, middle.Number, oldest.Number);
        first.Items[0].SellerNames.Should().Equal("Lumen Goods", "Harbor Supply Co.", "Kestrel Outfitters");
        first.Items[0].StatusText.Should().Be("Awaiting payment");
        first.Items[1].StatusText.Should().Be("Expired");
        var paid = second.Items.Single();
        paid.SellerNames.Should().Equal("Lumen Goods", "Kestrel Outfitters");
        paid.ItemCount.Should().Be(3);
        paid.StatusText.Should().Be("Processing");
        paid.Total.Should().Be(oldest.Total);

        (await session.GetAsync("/api/v1/orders?pageSize=0")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("BR", "BR-ORD-02")]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_02_the_order_page_follows_each_line_from_awaiting_its_shop_to_confirmed_or_not_supplied_and_refunded()
    {
        var (me, session) = await factory.SignedInCustomerAsync();
        await using var host = OrderJobHost.Start(factory);
        var kestrel = LineSpec.Kestrel(16.22m, 2);
        var order = await factory.OrderAsync(me, OrderStatus.Paid, Now, LineSpec.Own(18.50m), LineSpec.Harbor(49.90m), kestrel);

        // Harbor confirms at once; Kestrel received the order and has not decided yet.
        await host.RunAsync(order);
        var processing = await (await session.GetAsync($"/api/v1/orders/{order.Number}")).ReadAsync<OrderDetailDto>();

        processing.Shipments.Select(s => s.SellerName).Should().Equal("Lumen Goods", "Harbor Supply Co.", "Kestrel Outfitters");
        processing.Shipments.SelectMany(s => s.Lines).Select(l => l.StatusText)
            .Should().Equal("Confirmed", "Confirmed by Harbor Supply Co.", "Awaiting Kestrel Outfitters");
        processing.Status.Should().Be(OrderStatus.Paid);
        processing.StatusText.Should().Be("Processing");
        processing.IsUpdating.Should().BeTrue("the page keeps refreshing while a line waits for its shop");
        processing.Payment!.CardLast4.Should().Be("1111");
        processing.Refunds.Should().BeEmpty();

        host.Kestrel.Decisions[kestrel.Item] = ("REJECTED", $"OUT_OF_STOCK:{kestrel.Item}");
        host.Clock.Advance(host.Options.PollInterval);
        await host.RunAsync(order);
        var response = await session.GetAsync($"/api/v1/orders/{order.Number}");
        var final = await response.ReadAsync<OrderDetailDto>();

        // The refund is recorded; the refund job pays it back moments later.
        var kestrelLine = final.Shipments.Single(s => s.SellerName == "Kestrel Outfitters").Lines.Single();
        kestrelLine.StatusText.Should().Be("Not supplied, refund pending");
        kestrelLine.RefundedAmount.Should().Be(35.04m);
        final.Shipments.Single(s => s.SellerName == "Kestrel Outfitters").FeeRefunded.Should().BeTrue();
        final.StatusText.Should().Be("Partial refund pending");
        final.IsUpdating.Should().BeTrue("the page refreshes until the refund is paid back");
        final.Totals.Refunded.Should().Be(40.03m);
        final.Totals.NetPaid.Should().Be(order.Total - 40.03m);
        final.Refunds.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Amount = 40.03m, ShippingAmount = 4.99m, Status = RefundStatus.Pending, LineIds = new[] { kestrelLine.Id },
            // BR-ORD-03: the line's part is split into its subtotal and its tax, so the page can label the tax.
            Lines = new[] { new { LineId = kestrelLine.Id, Subtotal = 32.44m, Tax = 2.60m, Amount = 35.04m } },
            TaxAmount = 2.60m,
        });

        // On the wire: snake_case statuses, two-decimal money, a Lumen media URL for the image.
        var json = await response.ReadJsonAsync();
        json.GetProperty("status").GetString().Should().Be("partially_refunded");
        json.GetProperty("totals").GetProperty("refunded").GetRawText().Should().Be("40.03");
        var wireLine = json.GetProperty("shipments")[2].GetProperty("lines")[0];
        wireLine.GetProperty("status").GetString().Should().Be("rejected");
        wireLine.GetProperty("imageUrl").GetString().Should().MatchRegex("/media/[0-9a-f-]{36}/0\\?v=[0-9a-f]{12}$");
        json.GetProperty("refunds")[0].GetProperty("shippingAmount").GetRawText().Should().Be("4.99");
        json.GetProperty("refunds")[0].GetProperty("taxAmount").GetRawText().Should().Be("2.60");
        json.GetProperty("refunds")[0].GetProperty("lines")[0].GetProperty("subtotal").GetRawText().Should().Be("32.44");
        json.GetProperty("shipments")[1].GetProperty("lines")[0].GetProperty("statusText").ValueKind.Should().Be(JsonValueKind.String);

        // Paid back (what the refund job records): the line and the order say so.
        await using (var db = factory.NewContext())
        {
            await db.Refunds.Where(r => r.OrderId == order.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, RefundStatus.Succeeded).SetProperty(r => r.CompletedUtc, Now.AddMinutes(1)), Ct);
        }

        var paidBack = await (await session.GetAsync($"/api/v1/orders/{order.Number}")).ReadAsync<OrderDetailDto>();
        paidBack.Shipments.Single(s => s.SellerName == "Kestrel Outfitters").Lines.Single().StatusText.Should().Be("Not supplied, refunded $40.03");
        paidBack.StatusText.Should().Be("Partially refunded");
        paidBack.IsUpdating.Should().BeFalse();
        var history = await (await session.GetAsync("/api/v1/orders")).ReadAsync<PagedResult<OrderSummaryDto>>();
        history.Items.Single(o => o.Number == order.Number).StatusText.Should().Be("Partially refunded");
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_a_refund_of_two_lines_lists_them_in_line_order_each_split_into_its_subtotal_and_tax()
    {
        var (me, session) = await factory.SignedInCustomerAsync();
        var order = await factory.OrderAsync(me, OrderStatus.Paid, Now, LineSpec.Own(18.50m), LineSpec.Kestrel(16.22m, 2), LineSpec.Kestrel(9.99m, 3));

        // Both Kestrel lines rejected together: one refund of what the calculator says is owed for the two, its lines
        // stored last line first, so only the reader's ordering can put them back in line order.
        await using (var db = factory.NewContext())
        {
            var tracked = await db.Orders.Include(o => o.Lines).Include(o => o.Shipments).SingleAsync(o => o.Id == order.Id, Ct);
            var kestrelLines = tracked.Lines.Where(l => l.Source == ProductSources.Kestrel).OrderByDescending(l => l.LineNumber).ToList();
            var breakdown = RefundCalculator.Calculate(
                new PaidOrderSnapshot(
                    [.. tracked.Lines.Select(l => new PaidOrderLine(l.Id, l.Source, l.LineSubtotal, l.Tax, l.Status))],
                    [.. tracked.Shipments.Select(s => new PaidShipment(s.Source, s.Fee, s.FeeRefunded))],
                    tracked.Total,
                    tracked.RefundedTotal),
                [.. kestrelLines.Select(l => l.Id)]);
            var shipment = tracked.Shipments.Single(s => s.Source == ProductSources.Kestrel);
            var refund = new Refund
            {
                OrderId = order.Id,
                PaymentId = await LineRejection.SucceededPaymentIdAsync(db, order.Id, Ct),
                Amount = breakdown.Total,
                ShipmentId = shipment.Id,
                ShippingAmount = breakdown.Shipping.Single().Fee,
                Reason = "Not supplied by Kestrel Outfitters (OUT_OF_STOCK)",
                CreatedUtc = Now,
                NextAttemptUtc = Now,
            };
            foreach (var line in kestrelLines)
            {
                var part = breakdown.Lines.Single(l => l.LineId == line.Id);
                refund.Lines.Add(new RefundLine { OrderId = order.Id, OrderLineId = line.Id, Amount = part.Amount });
                line.Status = OrderLineStatus.Rejected;
                line.RefundedAmount = part.Amount;
            }

            shipment.FeeRefunded = true;
            tracked.RefundedTotal += breakdown.Total;
            LineRejection.DeriveOrderStatus(tracked);
            db.Refunds.Add(refund);
            await db.SaveChangesAsync(Ct);
        }

        var response = await session.GetAsync($"/api/v1/orders/{order.Number}");
        var detail = await response.ReadAsync<OrderDetailDto>();

        var kestrel = detail.Shipments.Single(s => s.SellerName == "Kestrel Outfitters").Lines;
        kestrel.Select(l => l.LineNumber).Should().Equal(2, 3);
        var refunded = detail.Refunds.Should().ContainSingle().Subject;
        refunded.Lines.Should().BeEquivalentTo(
            [
                new { LineId = kestrel[0].Id, Subtotal = 32.44m, Tax = 2.60m, Amount = 35.04m },
                new { LineId = kestrel[1].Id, Subtotal = 29.97m, Tax = 2.40m, Amount = 32.37m },
            ],
            options => options.WithStrictOrdering());
        refunded.LineIds.Should().Equal(kestrel[0].Id, kestrel[1].Id);
        refunded.TaxAmount.Should().Be(5.00m).And.Be(refunded.Lines.Sum(l => l.Tax));
        refunded.ShippingAmount.Should().Be(4.99m);
        refunded.Amount.Should().Be(72.40m);
        (refunded.Lines.Sum(l => l.Subtotal) + refunded.TaxAmount + refunded.ShippingAmount).Should().Be(refunded.Amount);
        detail.Totals.Refunded.Should().Be(72.40m);

        // On the wire too, in line order.
        var wireLines = (await response.ReadJsonAsync()).GetProperty("refunds")[0].GetProperty("lines");
        wireLines.EnumerateArray().Select(l => l.GetProperty("tax").GetRawText()).Should().Equal("2.60", "2.40");

        // Paid back: the seller's fee is counted on the refund's last line, so the two lines add up to the refund.
        await using (var db = factory.NewContext())
        {
            await db.Refunds.Where(r => r.OrderId == order.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, RefundStatus.Succeeded).SetProperty(r => r.CompletedUtc, Now.AddMinutes(1)), Ct);
        }

        var paidBack = await (await session.GetAsync($"/api/v1/orders/{order.Number}")).ReadAsync<OrderDetailDto>();
        paidBack.Shipments.Single(s => s.SellerName == "Kestrel Outfitters").Lines.Select(l => l.StatusText)
            .Should().Equal("Not supplied, refunded $35.04", "Not supplied, refunded $37.36");
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_one_of_two_harbor_lines_rejected_refunds_its_subtotal_and_tax_but_not_the_shipping()
    {
        var (me, session) = await factory.SignedInCustomerAsync();
        await using var host = OrderJobHost.Start(factory);
        var outOfStock = LineSpec.Harbor(49.90m, 3);
        var order = await factory.OrderAsync(me, OrderStatus.Paid, Now, outOfStock, LineSpec.Harbor(12.00m));
        host.Harbor.Available[outOfStock.Item] = 1;

        await host.RunAsync(order);
        var detail = await (await session.GetAsync($"/api/v1/orders/{order.Number}")).ReadAsync<OrderDetailDto>();

        var harbor = detail.Shipments.Single();
        harbor.Lines.Select(l => l.StatusText).Should().Equal("Not supplied, refund pending", "Confirmed by Harbor Supply Co.");
        harbor.FeeRefunded.Should().BeFalse("the other Harbor line still ships");
        var refund = detail.Refunds.Should().ContainSingle().Subject;
        refund.Lines.Should().BeEquivalentTo([new { LineId = harbor.Lines[0].Id, Subtotal = 149.70m, Tax = 11.98m, Amount = 161.68m }]);
        refund.TaxAmount.Should().Be(11.98m);
        refund.ShippingAmount.Should().Be(0m);
        refund.Amount.Should().Be(161.68m);
        (refund.Lines.Sum(l => l.Subtotal) + refund.TaxAmount + refund.ShippingAmount).Should().Be(refund.Amount);
    }
}
