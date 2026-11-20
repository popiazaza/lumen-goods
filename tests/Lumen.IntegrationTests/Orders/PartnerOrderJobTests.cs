using Lumen.Api.Orders;
using Lumen.Api.Partners.Ports;
using Lumen.Domain.Catalog;
using Lumen.Domain.Orders;
using Lumen.IntegrationTests.Infrastructure;
using static Lumen.IntegrationTests.Orders.OrderingTestData;

namespace Lumen.IntegrationTests.Orders;

/// <summary>
/// The partner order job end to end (BR-ORD-01, BR-ORD-03, BR-ORD-04): the real job, gateways, HTTP clients and
/// PostgreSQL, with contract-written fake partners and a fake clock. Each test runs the job for its own orders.
/// </summary>
public sealed class PartnerOrderJobTests(LumenApiFactory factory)
{
    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_no_partner_is_called_for_an_order_that_is_not_paid()
    {
        await using var host = OrderJobHost.Start(factory);
        var customer = await factory.NewCustomerAsync();
        var declined = await factory.OrderAsync(customer, OrderStatus.AwaitingPayment, Now, LineSpec.Harbor(49.90m), LineSpec.Kestrel(16.22m));
        var expired = await factory.OrderAsync(customer, OrderStatus.Expired, Now, LineSpec.Harbor(49.90m));

        // Even a partner order row that should never exist before payment (a bug elsewhere) is not sent.
        await factory.SaveAsync(new PartnerOrder
        {
            OrderLineId = declined.Line(1).Id, Source = ProductSources.Harbor, ExternalRef = $"{declined.Number}-1", CreatedUtc = Now, NextAttemptUtc = Now,
        });

        var report = await host.RunAsync(declined, expired);
        host.Clock.Advance(TimeSpan.FromHours(1));
        await host.RunAsync(declined, expired);

        report.Steps.Should().BeEmpty();
        host.Harbor.Calls.Should().BeEmpty();
        host.Kestrel.Calls.Should().BeEmpty();
        (await factory.ReloadAsync(declined)).Line(1).PartnerOrder!.Status.Should().Be(PartnerOrderStatus.Pending);
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_harbor_accepted_but_the_answer_was_lost_the_resend_with_the_same_ref_gets_200_and_one_harbor_order()
    {
        await using var host = OrderJobHost.Start(factory);
        var order = await factory.PaidOrderAsync(Now, LineSpec.Own(), LineSpec.Harbor(24.95m, 2));
        var reference = $"{order.Number}-2";
        host.Harbor.LoseAnswers[reference] = 1;

        var first = await host.RunAsync(order);
        first.Steps.Should().Equal(PartnerOrderStep.RetryScheduled);
        var waiting = await factory.ReloadAsync(order);
        waiting.Line(2).Status.Should().Be(OrderLineStatus.AwaitingPartner);
        waiting.Line(2).PartnerOrder!.Status.Should().Be(PartnerOrderStatus.Submitted);
        waiting.Line(2).PartnerOrder!.LastError.Should().Contain("timed out");
        (await factory.OrderErrorsAsync(ProductSources.Harbor, reference)).Should().ContainSingle().Which.Should().Contain("timed out");

        host.Clock.Advance(host.Options.RetryDelay);
        var second = await host.RunAsync(order);

        second.Steps.Should().Equal(PartnerOrderStep.Confirmed);
        host.Harbor.CallsFor(reference).Should().HaveCount(2).And.OnlyContain(c => c.Method == "POST" && c.Path == "/v2/orders");
        host.Harbor.Orders.Keys.Count(k => k == reference).Should().Be(1, "the resend found the original order and did not order again");

        var confirmed = await factory.ReloadAsync(order);
        var partnerOrder = confirmed.Line(2).PartnerOrder!;
        partnerOrder.Status.Should().Be(PartnerOrderStatus.Confirmed);
        partnerOrder.PartnerOrderId.Should().Be(host.Harbor.Orders[reference]);
        partnerOrder.ExternalRef.Should().Be(reference);
        partnerOrder.Attempts.Should().Be(2);
        confirmed.Line(2).Status.Should().Be(OrderLineStatus.Confirmed);
        confirmed.Status.Should().Be(OrderStatus.Completed);
        confirmed.Refunds.Should().BeEmpty();
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_a_kestrel_resend_after_a_lost_answer_adopts_the_ticket_from_409_and_polls_it_to_confirmed()
    {
        await using var host = OrderJobHost.Start(factory);
        var kestrel = LineSpec.Kestrel(16.22m, 2);
        var order = await factory.PaidOrderAsync(Now, LineSpec.Own(), kestrel);
        var reference = $"{order.Number}-2";
        host.Kestrel.LoseAnswers[reference] = 1;
        host.Kestrel.Decisions[kestrel.Item] = ("CONFIRMED", null);

        (await host.RunAsync(order)).Steps.Should().Equal(PartnerOrderStep.RetryScheduled);
        host.Clock.Advance(host.Options.RetryDelay);
        (await host.RunAsync(order)).Steps.Should().Equal(PartnerOrderStep.Accepted);

        var adopted = (await factory.ReloadAsync(order)).Line(2).PartnerOrder!;
        adopted.Status.Should().Be(PartnerOrderStatus.Accepted);
        adopted.PartnerOrderId.Should().Be(host.Kestrel.TicketsByReference[reference]);
        adopted.AcceptedUtc.Should().Be(host.Clock.GetUtcNow());

        host.Clock.Advance(host.Options.PollInterval);
        (await host.RunAsync(order)).Steps.Should().Equal(PartnerOrderStep.Confirmed);

        host.Kestrel.CallsFor(reference).Select(c => c.Method).Should().Equal("POST", "POST", "GET");
        host.Kestrel.TicketsByReference.Keys.Count(k => k == reference).Should().Be(1);
        var confirmed = await factory.ReloadAsync(order);
        confirmed.Line(2).Status.Should().Be(OrderLineStatus.Confirmed);
        confirmed.Line(2).PartnerOrder!.Status.Should().Be(PartnerOrderStatus.Confirmed);
        confirmed.Line(2).PartnerOrder!.NextAttemptUtc.Should().BeNull();
        confirmed.Status.Should().Be(OrderStatus.Completed);
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_kestrel_rejects_the_line_40_03_is_refunded_and_the_lumen_line_goes_ahead()
    {
        await using var host = OrderJobHost.Start(factory);
        var kestrel = LineSpec.Kestrel(16.22m, 2);
        var order = await factory.PaidOrderAsync(Now, LineSpec.Own(18.50m), kestrel);
        host.Kestrel.Decisions[kestrel.Item] = ("REJECTED", $"OUT_OF_STOCK:{kestrel.Item}");

        (await host.RunAsync(order)).Steps.Should().Equal(PartnerOrderStep.Accepted);
        host.Clock.Advance(host.Options.PollInterval);
        (await host.RunAsync(order)).Steps.Should().Equal(PartnerOrderStep.Rejected);

        var after = await factory.ReloadAsync(order);
        var refund = after.Refunds.Should().ContainSingle().Subject;
        refund.Amount.Should().Be(40.03m);
        refund.ShippingAmount.Should().Be(4.99m);
        refund.ShipmentId.Should().Be(after.Shipment(ProductSources.Kestrel).Id);
        refund.Lines.Should().ContainSingle().Which.Should().BeEquivalentTo(new { OrderLineId = after.Line(2).Id, Amount = 35.04m });
        refund.Status.Should().Be(RefundStatus.Pending);
        refund.Reason.Should().Be($"Not supplied by Kestrel Outfitters (OUT_OF_STOCK:{kestrel.Item})");

        after.Line(2).Status.Should().Be(OrderLineStatus.Rejected);
        after.Line(2).RefundedAmount.Should().Be(35.04m);
        after.Line(2).StatusReason.Should().Be($"OUT_OF_STOCK:{kestrel.Item}");
        after.Line(2).PartnerOrder!.Status.Should().Be(PartnerOrderStatus.Rejected);
        after.Shipment(ProductSources.Kestrel).FeeRefunded.Should().BeTrue();
        after.Shipment(ProductSources.Lumen).FeeRefunded.Should().BeFalse();
        after.Line(1).Status.Should().Be(OrderLineStatus.Confirmed);
        after.RefundedTotal.Should().Be(40.03m);
        after.Status.Should().Be(OrderStatus.PartiallyRefunded);
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_two_harbor_lines_one_invalid_refunds_that_line_and_its_tax_and_keeps_the_shipping()
    {
        await using var host = OrderJobHost.Start(factory);
        var kept = LineSpec.Harbor(24.95m, 2);
        var invalid = LineSpec.Harbor(15.00m, 1);
        var order = await factory.PaidOrderAsync(Now, kept, invalid);
        host.Harbor.Invalid[invalid.Item] = true;

        var report = await host.RunAsync(order);

        report.Steps.Should().BeEquivalentTo([PartnerOrderStep.Confirmed, PartnerOrderStep.Rejected]);
        var after = await factory.ReloadAsync(order);
        after.Refunds.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Amount = 16.20m, ShippingAmount = 0m, ShipmentId = (Guid?)null });
        after.Line(1).Status.Should().Be(OrderLineStatus.Confirmed);
        after.Line(2).Status.Should().Be(OrderLineStatus.Rejected);
        after.Line(2).StatusReason.Should().StartWith("INVALID_ITEM");
        after.Shipment(ProductSources.Harbor).FeeRefunded.Should().BeFalse();
        after.RefundedTotal.Should().Be(16.20m);
        after.Status.Should().Be(OrderStatus.PartiallyRefunded);

        // An "invalid item" points at a catalogue problem: the admin sees it on the partner status page.
        (await factory.OrderErrorsAsync(ProductSources.Harbor, $"{order.Number}-2")).Should().ContainSingle().Which.Should().Contain("INVALID_ITEM");
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_when_every_line_is_rejected_the_refunds_equal_the_amount_charged_never_more()
    {
        await using var host = OrderJobHost.Start(factory);
        var harbor = LineSpec.Harbor(49.90m, 3);
        var kestrel = LineSpec.Kestrel(16.22m, 2);
        var order = await factory.PaidOrderAsync(Now, harbor, kestrel);
        host.Harbor.Available[harbor.Item] = 1;
        host.Kestrel.Decisions[kestrel.Item] = ("REJECTED", $"OUT_OF_STOCK:{kestrel.Item}");

        (await host.RunAsync(order)).Steps.Should().BeEquivalentTo([PartnerOrderStep.Rejected, PartnerOrderStep.Accepted]);
        host.Clock.Advance(host.Options.PollInterval);
        (await host.RunAsync(order)).Steps.Should().Equal(PartnerOrderStep.Rejected);

        // The same rejection seen again (a duplicate poll answer) refunds nothing more.
        var rejected = await factory.ReloadAsync(order);
        (await host.ApplyAsync(rejected, rejected.Line(2), new PartnerOrderResult(PartnerOrderOutcome.Rejected, Reason: "OUT_OF_STOCK", HttpStatus: 200)))
            .Should().Be(PartnerOrderStep.Ignored);
        host.Clock.Advance(TimeSpan.FromHours(2));
        (await host.RunAsync(order)).Steps.Should().BeEmpty();

        var after = await factory.ReloadAsync(order);
        after.Refunds.Should().HaveCount(2);
        after.Refunds.Sum(r => r.Amount).Should().Be(after.Total);
        after.RefundedTotal.Should().Be(after.Total);
        after.Line(1).StatusReason.Should().Be("INSUFFICIENT_STOCK: 1 available");
        after.Shipments.Should().OnlyContain(s => s.FeeRefunded);
        after.Status.Should().Be(OrderStatus.Refunded);
    }

    [Fact]
    [Trait("BR", "BR-ORD-04")]
    public async Task BR_ORD_04_harbor_down_from_payment_onward_the_line_is_refunded_after_30_minutes_other_lines_unaffected()
    {
        await using var host = OrderJobHost.Start(factory);
        var order = await factory.PaidOrderAsync(Now, LineSpec.Own(18.50m), LineSpec.Harbor(49.90m));
        var reference = $"{order.Number}-2";
        host.Harbor.Down = true;

        (await host.RunAsync(order)).Steps.Should().Equal(PartnerOrderStep.RetryScheduled);
        host.Clock.Advance(TimeSpan.FromMinutes(29));
        (await host.RunAsync(order)).Steps.Should().Equal(PartnerOrderStep.RetryScheduled);

        var waiting = await factory.ReloadAsync(order);
        waiting.Line(2).Status.Should().Be(OrderLineStatus.AwaitingPartner);
        waiting.Refunds.Should().BeEmpty();
        waiting.Line(2).PartnerOrder!.NextAttemptUtc.Should().BeOnOrBefore(Now.AddMinutes(30), "the last attempt is at the deadline");

        host.Clock.Advance(TimeSpan.FromMinutes(1));
        (await host.RunAsync(order)).Steps.Should().Equal(PartnerOrderStep.GivenUp);

        host.Harbor.CallsFor(reference).Should().HaveCount(3);
        var after = await factory.ReloadAsync(order);
        after.Line(2).PartnerOrder!.Status.Should().Be(PartnerOrderStatus.GivenUp);
        after.Line(2).Status.Should().Be(OrderLineStatus.Rejected);
        after.Line(2).StatusReason.Should().Be("Not accepted by Harbor Supply Co. within 30 minutes of payment");
        after.Refunds.Should().ContainSingle().Which.Amount.Should().Be(49.90m + 3.99m + 4.99m);
        after.Line(1).Status.Should().Be(OrderLineStatus.Confirmed);
        after.Status.Should().Be(OrderStatus.PartiallyRefunded);
        (await factory.OrderErrorsAsync(ProductSources.Harbor, reference)).Should().HaveCount(4).And.Contain(m => m.Contains("given up"));

        // Harbor comes back: nothing is sent again for a partner order that was given up.
        host.Harbor.Down = false;
        host.Clock.Advance(TimeSpan.FromHours(1));
        (await host.RunAsync(order)).Steps.Should().BeEmpty();
        host.Harbor.CallsFor(reference).Should().HaveCount(3);
    }

    [Fact]
    [Trait("BR", "BR-ORD-04")]
    public async Task BR_ORD_04_a_kestrel_ticket_still_received_after_24_hours_needs_attention_and_nothing_is_refunded()
    {
        await using var host = OrderJobHost.Start(factory);
        var order = await factory.PaidOrderAsync(Now, LineSpec.Own(), LineSpec.Kestrel(16.22m, 2));

        (await host.RunAsync(order)).Steps.Should().Equal(PartnerOrderStep.Accepted);
        host.Clock.Advance(TimeSpan.FromHours(23));
        var before = await host.RunAsync(order);
        before.Steps.Should().Equal(PartnerOrderStep.StillWaiting);
        before.FlaggedNeedsAttention.Should().Be(0);

        host.Clock.Advance(TimeSpan.FromHours(1));
        var report = await host.RunAsync(order);

        report.FlaggedNeedsAttention.Should().Be(1);
        var after = await factory.ReloadAsync(order);
        after.Line(2).Status.Should().Be(OrderLineStatus.NeedsAttention);
        after.Line(2).StatusReason.Should().Be("No decision from Kestrel Outfitters 24 hours after it accepted the order");
        after.Line(2).PartnerOrder!.Status.Should().Be(PartnerOrderStatus.Accepted);
        after.Line(2).PartnerOrder!.NextAttemptUtc.Should().NotBeNull("polling goes on, so a late decision still lands");
        after.Refunds.Should().BeEmpty();
        after.RefundedTotal.Should().Be(0m);
        after.Status.Should().Be(OrderStatus.Paid);

        // A decision that finally arrives is still applied.
        host.Kestrel.Decisions[after.Line(2).SourceProductId] = ("CONFIRMED", null);
        host.Clock.Advance(host.Options.MaxPollInterval);
        (await host.RunAsync(order)).Steps.Should().Equal(PartnerOrderStep.Confirmed);
        var confirmed = await factory.ReloadAsync(order);
        confirmed.Line(2).Status.Should().Be(OrderLineStatus.Confirmed);
        confirmed.Status.Should().Be(OrderStatus.Completed);
    }
}
