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
        waiting.Line(2).PartnerOrder!.OutcomeUnknown.Should().BeTrue("Harbor may have the order");
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
        partnerOrder.OutcomeUnknown.Should().BeFalse("Harbor's 200 settled the lost answer");
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
    public async Task BR_ORD_04_harbor_unreachable_from_payment_onward_the_line_is_refunded_after_30_minutes_other_lines_unaffected()
    {
        await using var host = OrderJobHost.Start(factory);
        var order = await factory.PaidOrderAsync(Now, LineSpec.Own(18.50m), LineSpec.Harbor(49.90m));
        var reference = $"{order.Number}-2";
        host.Harbor.Unreachable = true;

        (await host.RunAsync(order)).Steps.Should().Equal(PartnerOrderStep.RetryScheduled);
        host.Clock.Advance(TimeSpan.FromMinutes(29));
        (await host.RunAsync(order)).Steps.Should().Equal(PartnerOrderStep.RetryScheduled);

        var waiting = await factory.ReloadAsync(order);
        waiting.Line(2).Status.Should().Be(OrderLineStatus.AwaitingPartner);
        waiting.Refunds.Should().BeEmpty();
        waiting.Line(2).PartnerOrder!.NextAttemptUtc.Should().BeOnOrBefore(Now.AddMinutes(30), "the last attempt is at the deadline");
        waiting.Line(2).PartnerOrder!.OutcomeUnknown.Should().BeFalse("no send ever reached Harbor");

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
        host.Harbor.Unreachable = false;
        host.Clock.Advance(TimeSpan.FromHours(1));
        (await host.RunAsync(order)).Steps.Should().BeEmpty();
        host.Harbor.CallsFor(reference).Should().HaveCount(3);
    }

    [Fact]
    [Trait("BR", "BR-ORD-04")]
    public async Task BR_ORD_04_harbor_answering_503_for_30_minutes_may_have_the_order_so_the_line_needs_an_admin_and_nothing_is_refunded()
    {
        await using var host = OrderJobHost.Start(factory);
        var order = await factory.PaidOrderAsync(Now, LineSpec.Own(18.50m), LineSpec.Harbor(49.90m));
        var reference = $"{order.Number}-2";
        host.Harbor.Down = true;

        await host.RunAsync(order);
        host.Clock.Advance(TimeSpan.FromMinutes(30));
        var report = await host.RunAsync(order);

        report.Steps.Should().Equal(PartnerOrderStep.NeedsAttention);
        var flagged = await factory.ReloadAsync(order);
        flagged.Line(2).Status.Should().Be(OrderLineStatus.NeedsAttention);
        flagged.Line(2).StatusReason.Should().Contain("may have received the order");
        flagged.Line(2).PartnerOrder!.Status.Should().Be(PartnerOrderStatus.Submitted);
        flagged.Line(2).PartnerOrder!.OutcomeUnknown.Should().BeTrue();
        flagged.Line(2).PartnerOrder!.NextAttemptUtc.Should().Be(host.Clock.GetUtcNow() + host.Options.MaxRetryDelay);
        flagged.Refunds.Should().BeEmpty();
        flagged.RefundedTotal.Should().Be(0m);
        flagged.Status.Should().Be(OrderStatus.Paid);

        // Harbor comes back: the same reference is resent and settles it.
        host.Harbor.Down = false;
        host.Clock.Advance(host.Options.MaxRetryDelay);
        (await host.RunAsync(order)).Steps.Should().Equal(PartnerOrderStep.Confirmed);
        var settled = await factory.ReloadAsync(order);
        settled.Line(2).Status.Should().Be(OrderLineStatus.Confirmed);
        settled.Line(2).PartnerOrder!.OutcomeUnknown.Should().BeFalse();
        settled.Status.Should().Be(OrderStatus.Completed);
        host.Harbor.Orders.Keys.Count(k => k == reference).Should().Be(1);
    }

    [Fact]
    [Trait("BR", "BR-ORD-04")]
    public async Task BR_ORD_04_a_kestrel_400_to_a_resend_after_a_lost_answer_needs_an_admin_but_a_400_to_the_first_send_is_refunded()
    {
        await using var host = OrderJobHost.Start(factory);
        var unclear = LineSpec.Kestrel(16.22m, 2);
        var invalid = LineSpec.Kestrel(9.00m);
        var order = await factory.PaidOrderAsync(Now, LineSpec.Own(18.50m), unclear);
        var other = await factory.PaidOrderAsync(Now, LineSpec.Own(18.50m), invalid);
        host.Kestrel.LoseAnswers[$"{order.Number}-2"] = 1;
        host.Kestrel.Unknown[invalid.Item] = true;

        (await host.RunAsync(order, other)).Steps.Should().BeEquivalentTo([PartnerOrderStep.RetryScheduled, PartnerOrderStep.Rejected]);

        // Kestrel took the first send, then stopped knowing the product: the resend gets 400, which says nothing about
        // the order the first send may have created.
        host.Kestrel.Unknown[unclear.Item] = true;
        host.Clock.Advance(host.Options.RetryDelay);
        (await host.RunAsync(order)).Steps.Should().Equal(PartnerOrderStep.NeedsAttention);

        var flagged = await factory.ReloadAsync(order);
        flagged.Line(2).Status.Should().Be(OrderLineStatus.NeedsAttention);
        flagged.Line(2).StatusReason.Should().Contain("refused a resend").And.Contain("INVALID_REQUEST");
        flagged.Line(2).PartnerOrder!.OutcomeUnknown.Should().BeTrue();
        flagged.Line(2).PartnerOrder!.NextAttemptUtc.Should().BeNull("Kestrel will refuse it however often it is sent; an admin decides");
        flagged.Refunds.Should().BeEmpty();

        // The other order's first and only send was refused: Kestrel cannot have it, so it is refunded at once.
        var refunded = await factory.ReloadAsync(other);
        refunded.Line(2).Status.Should().Be(OrderLineStatus.Rejected);
        refunded.Refunds.Should().ContainSingle().Which.Amount.Should().Be(9.00m + 0.72m + 4.99m);
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_when_saving_harbors_acceptance_fails_the_retry_adopts_the_same_order_and_harbor_orders_once()
    {
        await using var host = OrderJobHost.Start(factory);
        var order = await factory.PaidOrderAsync(Now, LineSpec.Own(), LineSpec.Harbor(24.95m));
        var reference = $"{order.Number}-2";
        host.SaveFailures.FailNext();

        var first = await host.RunAsync(order);

        first.Failed.Should().Be(1);
        host.Harbor.Orders.Should().ContainKey(reference, "Harbor created the order before the save failed");
        var unsaved = (await factory.ReloadAsync(order)).Line(2).PartnerOrder!;
        unsaved.Status.Should().Be(PartnerOrderStatus.Submitted);
        unsaved.OutcomeUnknown.Should().BeTrue("the claim marks a send unknown until its result is saved");
        (await host.RunAsync(order)).Steps.Should().BeEmpty("the claim still reserves it");

        host.Clock.Advance(host.Options.ClaimTimeout);
        (await host.RunAsync(order)).Steps.Should().Equal(PartnerOrderStep.Confirmed);

        host.Harbor.CallsFor(reference).Should().HaveCount(2);
        host.Harbor.Orders.Keys.Count(k => k == reference).Should().Be(1);
        var saved = (await factory.ReloadAsync(order)).Line(2).PartnerOrder!;
        saved.Status.Should().Be(PartnerOrderStatus.Confirmed);
        saved.PartnerOrderId.Should().Be(host.Harbor.Orders[reference]);
        saved.OutcomeUnknown.Should().BeFalse();
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_two_kestrel_lines_rejected_in_separate_runs_refund_the_shipping_once_with_the_second()
    {
        await using var host = OrderJobHost.Start(factory);
        var first = LineSpec.Kestrel(16.22m, 2);
        var second = LineSpec.Kestrel(9.00m);
        var order = await factory.PaidOrderAsync(Now, LineSpec.Own(18.50m), first, second);
        host.Kestrel.Decisions[first.Item] = ("REJECTED", $"OUT_OF_STOCK:{first.Item}");

        await host.RunAsync(order);
        host.Clock.Advance(host.Options.PollInterval);
        (await host.RunAsync(order)).Steps.Should().BeEquivalentTo([PartnerOrderStep.Rejected, PartnerOrderStep.StillWaiting]);
        var half = await factory.ReloadAsync(order);
        half.Refunds.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Amount = 35.04m, ShippingAmount = 0m, ShipmentId = (Guid?)null });
        half.Shipment(ProductSources.Kestrel).FeeRefunded.Should().BeFalse("one Kestrel line still goes ahead");

        host.Kestrel.Decisions[second.Item] = ("REJECTED", $"OUT_OF_STOCK:{second.Item}");
        host.Clock.Advance(host.Options.MaxPollInterval);
        (await host.RunAsync(order)).Steps.Should().Equal(PartnerOrderStep.Rejected);

        var after = await factory.ReloadAsync(order);
        var refunds = after.Refunds.OrderBy(r => r.CreatedUtc).ToList();
        refunds.Should().HaveCount(2);
        refunds[1].Should().BeEquivalentTo(new { Amount = 9.00m + 0.72m + 4.99m, ShippingAmount = 4.99m, ShipmentId = (Guid?)after.Shipment(ProductSources.Kestrel).Id });
        refunds.Count(r => r.ShipmentId is not null).Should().Be(1);
        after.Shipment(ProductSources.Kestrel).FeeRefunded.Should().BeTrue();
        after.RefundedTotal.Should().Be(35.04m + 14.71m);
        after.Status.Should().Be(OrderStatus.PartiallyRefunded);
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_a_100_character_ticket_is_stored_and_polled_exactly_and_a_longer_one_than_lumen_stores_is_refused_not_cut()
    {
        await using var host = OrderJobHost.Start(factory);
        var item = LineSpec.Kestrel(16.22m);
        var order = await factory.PaidOrderAsync(Now, item);
        var ticket = "tk-" + string.Concat(Enumerable.Repeat("0123456789abcdefghijklmnopqrstuvwxyz", 3))[..97];
        host.Kestrel.NewTicket = () => ticket;
        host.Kestrel.Decisions[item.Item] = ("CONFIRMED", null);

        (await host.RunAsync(order)).Steps.Should().Equal(PartnerOrderStep.Accepted);
        (await factory.ReloadAsync(order)).Line(1).PartnerOrder!.PartnerOrderId.Should().Be(ticket).And.HaveLength(100);
        host.Clock.Advance(host.Options.PollInterval);
        (await host.RunAsync(order)).Steps.Should().Equal(PartnerOrderStep.Confirmed);
        host.Kestrel.CallsFor($"{order.Number}-1").Last().Path.Should().Be($"/orders/{ticket}");

        var overlong = await factory.PaidOrderAsync(Now, LineSpec.Kestrel(16.22m));
        host.Kestrel.NewTicket = () => new string('t', 250);
        (await host.RunAsync(overlong)).Steps.Should().Equal(PartnerOrderStep.RetryScheduled);

        var refused = (await factory.ReloadAsync(overlong)).Line(1).PartnerOrder!;
        refused.PartnerOrderId.Should().BeNull();
        refused.Status.Should().Be(PartnerOrderStatus.Submitted);
        refused.OutcomeUnknown.Should().BeTrue("Kestrel took the order; Lumen only could not keep its ticket");
        refused.LastError.Should().Contain("250 characters").And.Contain("never cut");
        (await factory.OrderErrorsAsync(ProductSources.Kestrel, $"{overlong.Number}-1")).Should().ContainSingle().Which.Should().Contain("250 characters");
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
