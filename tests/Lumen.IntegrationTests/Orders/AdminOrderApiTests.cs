using System.Net;
using Lumen.Api.Catalog;
using Lumen.Api.Orders;
using Lumen.Api.Payments;
using Lumen.Domain.Orders;
using Lumen.IntegrationTests.Catalog;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static Lumen.IntegrationTests.Orders.OrderingTestData;

namespace Lumen.IntegrationTests.Orders;

/// <summary>
/// The admin orders pages over HTTP (spec page 10; BR-ADM-01, BR-ORD-04): filters for status and date, the detail with
/// payment events and partner orders, and resolving a line that needs attention.
/// </summary>
public sealed class AdminOrderApiTests(LumenApiFactory factory)
{
    private static readonly object Reject = new { decision = "reject", note = "Kestrel support: lost in the warehouse" };

    [Fact]
    [Trait("BR", "BR-ADM-01")]
    public async Task BR_ADM_01_the_admin_order_endpoints_need_the_admin_role()
    {
        var order = await factory.PaidOrderAsync(Now, LineSpec.Own(), LineSpec.Kestrel(16.22m));
        var routes = new[] { "/api/v1/admin/orders", $"/api/v1/admin/orders/{order.Number}" };
        var resolve = $"/api/v1/admin/orders/{order.Number}/lines/{order.Line(2).Id}/resolve";

        var anonymous = factory.CreateSession();
        await anonymous.StartAsync();
        foreach (var route in routes)
        {
            (await anonymous.GetAsync(route)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        (await anonymous.PostAsJsonAsync(resolve, Reject)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var (_, customer) = await factory.SignedInCustomerAsync();
        foreach (var route in routes)
        {
            (await customer.GetAsync(route)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        (await customer.PostAsJsonAsync(resolve, Reject)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await factory.ReloadAsync(order)).Line(2).Status.Should().Be(OrderLineStatus.AwaitingPartner);
    }

    [Fact]
    public async Task The_list_filters_by_status_and_placement_date_newest_first_and_pages()
    {
        // Placed on days no other test uses, so the date filter isolates them.
        var day = new DateTimeOffset(2011, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var customer = await factory.NewCustomerAsync();
        var lateFirstDay = await factory.OrderAsync(customer, OrderStatus.AwaitingPayment, day.AddHours(23).AddMinutes(32), LineSpec.Own());
        var paid = await factory.OrderAsync(customer, OrderStatus.Paid, day.AddDays(1).AddMinutes(32), LineSpec.Own(), LineSpec.Harbor(49.90m));
        var expired = await factory.OrderAsync(customer, OrderStatus.Expired, day.AddDays(2).AddHours(12), LineSpec.Kestrel(16.22m));
        var admin = await factory.SignedInAdminAsync();

        async Task<PagedResult<AdminOrderSummaryDto>> ListAsync(string query)
        {
            var response = await admin.GetAsync($"/api/v1/admin/orders?{query}");
            response.StatusCode.Should().Be(HttpStatusCode.OK, query);
            return await response.ReadAsync<PagedResult<AdminOrderSummaryDto>>();
        }

        (await ListAsync("from=2011-03-01&to=2011-03-03")).Items.Select(o => o.Number)
            .Should().Equal(expired.Number, paid.Number, lateFirstDay.Number);
        (await ListAsync("from=2011-03-01&to=2011-03-01")).Items.Select(o => o.Number).Should().Equal(lateFirstDay.Number);

        var paidOnly = await ListAsync("status=paid&from=2011-03-01&to=2011-03-03");
        var row = paidOnly.Items.Should().ContainSingle().Subject;
        row.Number.Should().Be(paid.Number);
        row.StatusText.Should().Be("Processing");
        row.SellerNames.Should().Equal("Lumen Goods", "Harbor Supply Co.");
        row.PaymentMethod.Should().Be(PaymentMethod.Card);
        row.CustomerEmail.Should().EndWith("@example.test");
        row.Total.Should().Be(paid.Total);
        row.NeedsAttention.Should().BeFalse();

        (await ListAsync("status=paid&status=expired&from=2011-03-01&to=2011-03-03")).Items.Should().HaveCount(2);
        (await ListAsync("status=paid,awaiting_payment&from=2011-03-01&to=2011-03-03")).Items.Should().HaveCount(2);

        var secondPage = await ListAsync("from=2011-03-01&to=2011-03-03&pageSize=2&page=2");
        secondPage.TotalCount.Should().Be(3);
        secondPage.TotalPages.Should().Be(2);
        secondPage.Items.Select(o => o.Number).Should().Equal(lateFirstDay.Number);

        // A page in Los Angeles sends its own midnight: 2 March 00:00 -08:00 is 08:00 UTC, after the 00:30 UTC order.
        (await ListAsync($"from={Uri.EscapeDataString("2011-03-02T00:00:00-08:00")}&to=2011-03-03")).Items.Select(o => o.Number)
            .Should().Equal(expired.Number);

        (await admin.GetAsync("/api/v1/admin/orders?status=shipped")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.GetAsync("/api/v1/admin/orders?from=03/01/2011")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.GetAsync("/api/v1/admin/orders?from=2011-03-03&to=2011-03-01")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_detail_shows_payment_events_oldest_first_and_each_partner_order()
    {
        await using var host = OrderJobHost.Start(factory);
        var order = await factory.PaidOrderAsync(Now, LineSpec.Own(), LineSpec.Harbor(49.90m), LineSpec.Kestrel(16.22m));
        await using (var db = factory.NewContext())
        {
            var paymentId = await db.Payments.Where(p => p.OrderId == order.Id).Select(p => p.Id).SingleAsync(Ct);
            db.PaymentEvents.AddRange(
                new PaymentEvent { PaymentId = paymentId, OrderId = order.Id, OccurredUtc = Now.AddSeconds(2), Type = PaymentEventType.Approved, Amount = order.Total },
                new PaymentEvent { PaymentId = paymentId, OrderId = order.Id, OccurredUtc = Now.AddSeconds(-5), Type = PaymentEventType.Started, Amount = order.Total, Detail = "Card: visa ending 1111" });
            await db.SaveChangesAsync(Ct);
        }

        await host.RunAsync(order);
        var admin = await factory.SignedInAdminAsync();
        var detail = await (await admin.GetAsync($"/api/v1/admin/orders/{order.Number}")).ReadAsync<AdminOrderDto>();

        detail.Order.Number.Should().Be(order.Number);
        detail.CustomerEmail.Should().EndWith("@example.test");
        detail.PaymentEvents.Select(e => e.Type).Should().Equal(PaymentEventType.Started, PaymentEventType.Approved);
        detail.PartnerOrders.Select(p => (p.LineNumber, p.SellerName, p.Status))
            .Should().Equal((2, "Harbor Supply Co.", PartnerOrderStatus.Confirmed), (3, "Kestrel Outfitters", PartnerOrderStatus.Accepted));
        var kestrel = detail.PartnerOrders[1];
        kestrel.ExternalRef.Should().Be($"{order.Number}-3");
        kestrel.PartnerOrderId.Should().Be(host.Kestrel.TicketsByReference[kestrel.ExternalRef]);
        kestrel.Attempts.Should().Be(1);
        kestrel.NextAttemptUtc.Should().NotBeNull();
        detail.PartnerOrders[0].PartnerOrderId.Should().Be(host.Harbor.Orders[$"{order.Number}-2"]);

        (await admin.GetAsync($"/api/v1/admin/orders/{new OrderNumberGenerator().Next()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("BR", "BR-ORD-04")]
    public async Task BR_ORD_04_an_admin_rejects_a_delayed_line_it_is_refunded_and_its_ticket_is_no_longer_polled()
    {
        await using var host = OrderJobHost.Start(factory);
        var order = await DelayedKestrelOrderAsync(host);
        var admin = await factory.SignedInAdminAsync();

        var attention = await (await admin.GetAsync("/api/v1/admin/orders?status=needs_attention&from=2026-11-19&to=2026-11-20"))
            .ReadAsync<PagedResult<AdminOrderSummaryDto>>();
        attention.Items.Should().Contain(o => o.Number == order.Number && o.NeedsAttention && o.Status == OrderStatus.Paid);
        var before = await (await admin.GetAsync($"/api/v1/admin/orders/{order.Number}")).ReadAsync<AdminOrderDto>();
        before.PartnerOrders.Single().NeedsAttention.Should().BeTrue();
        before.Order.Shipments.Single(s => s.SellerName == "Kestrel Outfitters").Lines.Single().StatusText.Should().Be("Delayed");

        var response = await admin.PostAsJsonAsync($"/api/v1/admin/orders/{order.Number}/lines/{order.Line(2).Id}/resolve", Reject);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var resolved = await response.ReadAsync<AdminOrderDto>();
        resolved.Order.Shipments.Single(s => s.SellerName == "Kestrel Outfitters").Lines.Single().StatusText.Should().Be("Not supplied, refunded $40.03");
        resolved.Order.StatusText.Should().Be("Partially refunded");
        resolved.Order.Refunds.Should().ContainSingle().Which.Reason.Should().Be("Not supplied by Kestrel Outfitters (rejected by an admin: Kestrel support: lost in the warehouse)");
        var partnerOrder = resolved.PartnerOrders.Single();
        partnerOrder.Status.Should().Be(PartnerOrderStatus.ClosedByAdmin);
        partnerOrder.NeedsAttention.Should().BeFalse();
        partnerOrder.NextAttemptUtc.Should().BeNull();

        // A second decision on the same line is refused; nothing is refunded twice.
        var again = await admin.PostAsJsonAsync($"/api/v1/admin/orders/{order.Number}/lines/{order.Line(2).Id}/resolve", Reject);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await again.ReadJsonAsync()).GetProperty("type").GetString().Should().Be(OrderProblemTypes.LineNotAwaitingDecision);

        var polls = host.Kestrel.CallsFor($"{order.Number}-2").Count;
        host.Clock.Advance(TimeSpan.FromHours(1));
        (await host.RunAsync(order)).Steps.Should().BeEmpty();
        host.Kestrel.CallsFor($"{order.Number}-2").Should().HaveCount(polls, "a closed partner order is not polled again");
        var after = await factory.ReloadAsync(order);
        after.Refunds.Should().ContainSingle().Which.Amount.Should().Be(40.03m);
        after.RefundedTotal.Should().Be(40.03m);
    }

    [Fact]
    [Trait("BR", "BR-ORD-04")]
    public async Task BR_ORD_04_an_admin_confirms_a_delayed_line_and_a_late_partner_answer_changes_nothing()
    {
        await using var host = OrderJobHost.Start(factory);
        var order = await DelayedKestrelOrderAsync(host);
        var admin = await factory.SignedInAdminAsync();
        var url = $"/api/v1/admin/orders/{order.Number}/lines/{order.Line(2).Id}/resolve";

        (await admin.PostAsJsonAsync(url, new { decision = "maybe" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync(url, new { note = "no decision" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"/api/v1/admin/orders/{order.Number}/lines/{Guid.NewGuid()}/resolve", new { decision = "confirm" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.PostAsJsonAsync($"/api/v1/admin/orders/{order.Number}/lines/{order.Line(1).Id}/resolve", new { decision = "confirm" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        var resolved = await (await admin.PostAsJsonAsync(url, new { decision = "confirm" })).ReadAsync<AdminOrderDto>();

        resolved.Order.Shipments.Single(s => s.SellerName == "Kestrel Outfitters").Lines.Single().StatusText.Should().Be("Confirmed by Kestrel Outfitters");
        resolved.Order.Status.Should().Be(OrderStatus.Completed);
        resolved.Order.Refunds.Should().BeEmpty();
        resolved.PartnerOrders.Single().Status.Should().Be(PartnerOrderStatus.ClosedByAdmin);

        // Kestrel rejecting it after all is a late answer: ignored, logged, nothing refunded.
        var stored = await factory.ReloadAsync(order);
        (await host.ApplyAsync(stored, stored.Line(2), new Lumen.Api.Partners.Ports.PartnerOrderResult(
                Lumen.Api.Partners.Ports.PartnerOrderOutcome.Rejected, Reason: "OUT_OF_STOCK", HttpStatus: 200)))
            .Should().Be(PartnerOrderStep.Ignored);
        var after = await factory.ReloadAsync(order);
        after.Line(2).Status.Should().Be(OrderLineStatus.Confirmed);
        after.Refunds.Should().BeEmpty();
    }

    /// <summary>A paid order with a Lumen line and a Kestrel line (2 × $16.22) that Kestrel accepted 24 hours ago and never decided.</summary>
    private async Task<Order> DelayedKestrelOrderAsync(OrderJobHost host)
    {
        var order = await factory.PaidOrderAsync(Now, LineSpec.Own(18.50m), LineSpec.Kestrel(16.22m, 2));
        await host.RunAsync(order);
        host.Clock.Advance(host.Options.NeedsAttentionAfter);
        (await host.RunAsync(order)).FlaggedNeedsAttention.Should().Be(1);
        return order;
    }
}
