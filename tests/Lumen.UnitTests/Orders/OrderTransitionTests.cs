using Lumen.Domain.Orders;

namespace Lumen.UnitTests.Orders;

/// <summary>
/// Every pair of statuses for each table, legal or not. The legal lists are written out here, independently of the
/// code, so changing a lifecycle means changing it in two places on purpose.
/// </summary>
public sealed class OrderTransitionTests
{
    public static TheoryData<OrderStatus, OrderStatus, bool> OrderMoves() => TransitionCases.AllPairs(
        (OrderStatus.AwaitingPayment, OrderStatus.PaymentProcessing),
        (OrderStatus.AwaitingPayment, OrderStatus.Expired),
        (OrderStatus.PaymentProcessing, OrderStatus.Paid),
        (OrderStatus.PaymentProcessing, OrderStatus.AwaitingPayment),
        (OrderStatus.PaymentProcessing, OrderStatus.Expired),
        (OrderStatus.Paid, OrderStatus.Completed),
        (OrderStatus.Paid, OrderStatus.PartiallyRefunded),
        (OrderStatus.Paid, OrderStatus.Refunded));

    public static TheoryData<OrderLineStatus, OrderLineStatus, bool> LineMoves() => TransitionCases.AllPairs(
        (OrderLineStatus.Pending, OrderLineStatus.Confirmed),
        (OrderLineStatus.Pending, OrderLineStatus.AwaitingPartner),
        (OrderLineStatus.AwaitingPartner, OrderLineStatus.Confirmed),
        (OrderLineStatus.AwaitingPartner, OrderLineStatus.Rejected),
        (OrderLineStatus.AwaitingPartner, OrderLineStatus.NeedsAttention),
        (OrderLineStatus.NeedsAttention, OrderLineStatus.Confirmed),
        (OrderLineStatus.NeedsAttention, OrderLineStatus.Rejected));

    public static TheoryData<PartnerOrderStatus, PartnerOrderStatus, bool> PartnerOrderMoves() => TransitionCases.AllPairs(
        (PartnerOrderStatus.Pending, PartnerOrderStatus.Submitted),
        (PartnerOrderStatus.Pending, PartnerOrderStatus.GivenUp),
        (PartnerOrderStatus.Submitted, PartnerOrderStatus.Confirmed),
        (PartnerOrderStatus.Submitted, PartnerOrderStatus.Accepted),
        (PartnerOrderStatus.Submitted, PartnerOrderStatus.Rejected),
        (PartnerOrderStatus.Submitted, PartnerOrderStatus.GivenUp),
        (PartnerOrderStatus.Accepted, PartnerOrderStatus.Confirmed),
        (PartnerOrderStatus.Accepted, PartnerOrderStatus.Rejected));

    [Theory]
    [MemberData(nameof(OrderMoves))]
    [Trait("BR", "BR-PAY-01")]
    [Trait("BR", "BR-STOCK-01")]
    public void BR_PAY_01_order_status_moves_only_along_its_table(OrderStatus from, OrderStatus to, bool legal) =>
        TransitionCases.AssertMove(OrderTransitions.Order, from, to, legal);

    [Theory]
    [MemberData(nameof(LineMoves))]
    [Trait("BR", "BR-ORD-02")]
    [Trait("BR", "BR-ORD-04")]
    public void BR_ORD_02_line_status_moves_only_along_its_table(OrderLineStatus from, OrderLineStatus to, bool legal) =>
        TransitionCases.AssertMove(OrderTransitions.Line, from, to, legal);

    [Theory]
    [MemberData(nameof(PartnerOrderMoves))]
    [Trait("BR", "BR-ORD-01")]
    [Trait("BR", "BR-ORD-04")]
    public void BR_ORD_01_partner_order_status_moves_only_along_its_table(PartnerOrderStatus from, PartnerOrderStatus to, bool legal) =>
        TransitionCases.AssertMove(OrderTransitions.PartnerOrder, from, to, legal);

    [Fact]
    [Trait("BR", "BR-PAY-01")]
    public void BR_PAY_01_only_an_order_awaiting_payment_can_be_claimed_by_a_payment() =>
        Enum.GetValues<OrderStatus>().Where(s => OrderTransitions.Order.CanMove(s, OrderStatus.PaymentProcessing))
            .Should().Equal(OrderStatus.AwaitingPayment);

    [Fact]
    [Trait("BR", "BR-PAY-01")]
    public void BR_PAY_01_a_paid_order_can_never_start_another_payment() =>
        FluentActions.Invoking(() => OrderTransitions.Order.Move(OrderStatus.Paid, OrderStatus.PaymentProcessing))
            .Should().Throw<InvalidOperationException>().WithMessage("OrderStatus cannot move from Paid to PaymentProcessing.");

    [Fact]
    [Trait("BR", "BR-STOCK-01")]
    public void BR_STOCK_01_a_paid_order_never_expires_and_an_expired_order_is_final()
    {
        OrderTransitions.Order.CanMove(OrderStatus.Paid, OrderStatus.Expired).Should().BeFalse();
        OrderTransitions.Order.IsFinal(OrderStatus.Expired).Should().BeTrue();
    }

    [Fact]
    [Trait("BR", "BR-STOCK-01")]
    public void BR_STOCK_01_a_declined_attempt_returns_the_order_to_awaiting_payment_so_it_can_be_retried() =>
        OrderTransitions.Order.Move(OrderStatus.PaymentProcessing, OrderStatus.AwaitingPayment).Should().Be(OrderStatus.AwaitingPayment);

    [Fact]
    [Trait("BR", "BR-ORD-02")]
    public void BR_ORD_02_own_lines_are_confirmed_on_payment_and_partner_lines_wait_for_the_partner() =>
        OrderTransitions.Line.NextStates(OrderLineStatus.Pending)
            .Should().BeEquivalentTo([OrderLineStatus.Confirmed, OrderLineStatus.AwaitingPartner]);

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public void BR_ORD_03_a_refunded_line_is_final_and_cannot_be_confirmed_later()
    {
        OrderTransitions.Line.IsFinal(OrderLineStatus.Rejected).Should().BeTrue();
        OrderTransitions.Line.CanMove(OrderLineStatus.Rejected, OrderLineStatus.Confirmed).Should().BeFalse();
    }

    [Fact]
    [Trait("BR", "BR-ORD-04")]
    public void BR_ORD_04_a_line_needing_attention_is_resolved_only_by_confirming_or_rejecting_it() =>
        OrderTransitions.Line.NextStates(OrderLineStatus.NeedsAttention)
            .Should().BeEquivalentTo([OrderLineStatus.Confirmed, OrderLineStatus.Rejected]);

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public void BR_ORD_01_a_partner_order_starts_pending_and_is_sent_before_anything_else_happens_to_it()
    {
        OrderTransitions.PartnerOrder.Initial.Should().Be(PartnerOrderStatus.Pending);
        OrderTransitions.PartnerOrder.CanMove(PartnerOrderStatus.Pending, PartnerOrderStatus.Confirmed).Should().BeFalse();
        OrderTransitions.PartnerOrder.CanMove(PartnerOrderStatus.Pending, PartnerOrderStatus.Rejected).Should().BeFalse();
    }

    [Fact]
    [Trait("BR", "BR-ORD-04")]
    public void BR_ORD_04_an_accepted_partner_order_is_never_given_up_because_the_goods_may_still_ship() =>
        OrderTransitions.PartnerOrder.CanMove(PartnerOrderStatus.Accepted, PartnerOrderStatus.GivenUp).Should().BeFalse();

    [Fact]
    [Trait("BR", "BR-ORD-04")]
    public void BR_ORD_04_a_partner_order_not_yet_accepted_can_be_given_up()
    {
        OrderTransitions.PartnerOrder.CanMove(PartnerOrderStatus.Pending, PartnerOrderStatus.GivenUp).Should().BeTrue();
        OrderTransitions.PartnerOrder.CanMove(PartnerOrderStatus.Submitted, PartnerOrderStatus.GivenUp).Should().BeTrue();
    }
}
