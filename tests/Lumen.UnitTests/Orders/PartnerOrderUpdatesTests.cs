using Lumen.Domain.Orders;

namespace Lumen.UnitTests.Orders;

public sealed class PartnerOrderUpdatesTests
{
    [Theory]
    [Trait("BR", "BR-ORD-01")]
    [InlineData(PartnerOrderStatus.Submitted, OrderLineStatus.AwaitingPartner, PartnerOrderStatus.Confirmed, PartnerOrderStatus.Confirmed, OrderLineStatus.Confirmed)]      // Harbor 201
    [InlineData(PartnerOrderStatus.Submitted, OrderLineStatus.AwaitingPartner, PartnerOrderStatus.Rejected, PartnerOrderStatus.Rejected, OrderLineStatus.Rejected)]        // Harbor 422
    [InlineData(PartnerOrderStatus.Submitted, OrderLineStatus.AwaitingPartner, PartnerOrderStatus.Accepted, PartnerOrderStatus.Accepted, OrderLineStatus.AwaitingPartner)] // Kestrel RECEIVED
    [InlineData(PartnerOrderStatus.Accepted, OrderLineStatus.AwaitingPartner, PartnerOrderStatus.Confirmed, PartnerOrderStatus.Confirmed, OrderLineStatus.Confirmed)]       // Kestrel CONFIRMED
    [InlineData(PartnerOrderStatus.Accepted, OrderLineStatus.AwaitingPartner, PartnerOrderStatus.Rejected, PartnerOrderStatus.Rejected, OrderLineStatus.Rejected)]         // Kestrel REJECTED
    public void BR_ORD_01_a_partner_answer_moves_the_partner_order_and_its_line_together(
        PartnerOrderStatus partnerOrder, OrderLineStatus line, PartnerOrderStatus answer, PartnerOrderStatus expectedPartnerOrder, OrderLineStatus expectedLine) =>
        PartnerOrderUpdates.ApplyPartnerAnswer(partnerOrder, line, answer)
            .Should().Be(new PartnerLineUpdate(expectedPartnerOrder, expectedLine));

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public void BR_ORD_01_a_repeated_answer_changes_nothing_and_is_not_reported_as_ignored()
    {
        // Kestrel says RECEIVED on every poll until it decides.
        var update = PartnerOrderUpdates.ApplyPartnerAnswer(PartnerOrderStatus.Accepted, OrderLineStatus.AwaitingPartner, PartnerOrderStatus.Accepted);

        update.Should().Be(new PartnerLineUpdate(PartnerOrderStatus.Accepted, OrderLineStatus.AwaitingPartner));
        update.Ignored.Should().BeFalse();
    }

    [Fact]
    [Trait("BR", "BR-ORD-04")]
    public void BR_ORD_04_the_partner_can_still_decide_a_line_that_needs_attention() =>
        PartnerOrderUpdates.ApplyPartnerAnswer(PartnerOrderStatus.Accepted, OrderLineStatus.NeedsAttention, PartnerOrderStatus.Confirmed)
            .Should().Be(new PartnerLineUpdate(PartnerOrderStatus.Confirmed, OrderLineStatus.Confirmed));

    [Theory]
    [Trait("BR", "BR-ORD-04")]
    [InlineData(OrderLineStatus.Confirmed)]
    [InlineData(OrderLineStatus.Rejected)]
    public void BR_ORD_04_an_admin_resolving_a_line_closes_its_partner_order(OrderLineStatus decision) =>
        PartnerOrderUpdates.ResolveByAdmin(PartnerOrderStatus.Accepted, OrderLineStatus.NeedsAttention, decision)
            .Should().Be(new PartnerLineUpdate(PartnerOrderStatus.ClosedByAdmin, decision));

    [Theory]
    [Trait("BR", "BR-ORD-04")]
    [InlineData(OrderLineStatus.Confirmed, PartnerOrderStatus.Confirmed)]
    [InlineData(OrderLineStatus.Confirmed, PartnerOrderStatus.Rejected)]
    [InlineData(OrderLineStatus.Rejected, PartnerOrderStatus.Confirmed)]
    [InlineData(OrderLineStatus.Rejected, PartnerOrderStatus.Accepted)]
    public void BR_ORD_04_a_late_answer_after_the_admin_closed_the_line_is_ignored_and_logged_not_thrown(
        OrderLineStatus adminDecision, PartnerOrderStatus lateAnswer)
    {
        var closed = PartnerOrderUpdates.ResolveByAdmin(PartnerOrderStatus.Accepted, OrderLineStatus.NeedsAttention, adminDecision);

        var late = PartnerOrderUpdates.ApplyPartnerAnswer(closed.PartnerOrder, closed.Line, lateAnswer);

        late.PartnerOrder.Should().Be(PartnerOrderStatus.ClosedByAdmin);
        late.Line.Should().Be(adminDecision);
        late.Ignored.Should().BeTrue();
        late.IgnoredBecause.Should().Be(
            $"Late partner answer {lateAnswer} ignored: the partner order is already ClosedByAdmin and the line {adminDecision}.");
    }

    [Theory]
    [Trait("BR", "BR-ORD-04")]
    [InlineData(PartnerOrderStatus.GivenUp, OrderLineStatus.Rejected, PartnerOrderStatus.Accepted)]  // accepted after Lumen gave up and refunded
    [InlineData(PartnerOrderStatus.Confirmed, OrderLineStatus.Confirmed, PartnerOrderStatus.Accepted)] // out-of-order poll answer
    [InlineData(PartnerOrderStatus.Rejected, OrderLineStatus.Rejected, PartnerOrderStatus.Confirmed)]
    public void BR_ORD_04_any_answer_for_a_final_partner_order_is_ignored(
        PartnerOrderStatus partnerOrder, OrderLineStatus line, PartnerOrderStatus answer)
    {
        var update = PartnerOrderUpdates.ApplyPartnerAnswer(partnerOrder, line, answer);

        update.Ignored.Should().BeTrue();
        update.PartnerOrder.Should().Be(partnerOrder);
        update.Line.Should().Be(line);
    }

    [Fact]
    [Trait("BR", "BR-ORD-04")]
    public void BR_ORD_04_only_a_line_that_needs_attention_is_resolved_by_an_admin() =>
        FluentActions.Invoking(() => PartnerOrderUpdates.ResolveByAdmin(PartnerOrderStatus.Accepted, OrderLineStatus.AwaitingPartner, OrderLineStatus.Rejected))
            .Should().Throw<InvalidOperationException>();

    [Fact]
    public void An_answer_that_does_not_fit_an_open_partner_order_is_a_bug_and_throws() =>
        // Confirmed for a partner order Lumen never marked as sent.
        FluentActions.Invoking(() => PartnerOrderUpdates.ApplyPartnerAnswer(PartnerOrderStatus.Pending, OrderLineStatus.AwaitingPartner, PartnerOrderStatus.Confirmed))
            .Should().Throw<InvalidOperationException>();

    [Theory]
    [InlineData(PartnerOrderStatus.GivenUp)]
    [InlineData(PartnerOrderStatus.ClosedByAdmin)]
    [InlineData(PartnerOrderStatus.Submitted)]
    public void Only_answers_a_partner_can_give_are_accepted(PartnerOrderStatus answer) =>
        FluentActions.Invoking(() => PartnerOrderUpdates.ApplyPartnerAnswer(PartnerOrderStatus.Accepted, OrderLineStatus.AwaitingPartner, answer))
            .Should().Throw<ArgumentOutOfRangeException>();
}
