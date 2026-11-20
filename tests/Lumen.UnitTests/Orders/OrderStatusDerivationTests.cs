using Lumen.Domain.Orders;

namespace Lumen.UnitTests.Orders;

public sealed class OrderStatusDerivationTests
{
    [Fact]
    [Trait("BR", "BR-ORD-02")]
    public void BR_ORD_02_an_order_with_a_line_still_awaiting_its_partner_is_processing() =>
        // Scenario 1: a Lumen line, an accepted Harbor line and a received Kestrel line.
        OrderStatusDerivation.Derive(OrderStatus.Paid,
                [OrderLineStatus.Confirmed, OrderLineStatus.Confirmed, OrderLineStatus.AwaitingPartner])
            .Should().Be(OrderStatus.Paid);

    [Fact]
    [Trait("BR", "BR-ORD-02")]
    public void BR_ORD_02_all_lines_final_and_one_refunded_is_partially_refunded() =>
        // Scenario 2.
        OrderStatusDerivation.Derive(OrderStatus.Paid, [OrderLineStatus.Confirmed, OrderLineStatus.Rejected, OrderLineStatus.Confirmed])
            .Should().Be(OrderStatus.PartiallyRefunded);

    [Theory]
    [Trait("BR", "BR-ORD-02")]
    [InlineData(OrderStatus.Completed, OrderLineStatus.Confirmed)]
    [InlineData(OrderStatus.Completed, OrderLineStatus.Confirmed, OrderLineStatus.Confirmed)]
    [InlineData(OrderStatus.Refunded, OrderLineStatus.Rejected)]
    [InlineData(OrderStatus.Refunded, OrderLineStatus.Rejected, OrderLineStatus.Rejected)]
    [InlineData(OrderStatus.PartiallyRefunded, OrderLineStatus.Rejected, OrderLineStatus.Confirmed)]
    [InlineData(OrderStatus.Paid, OrderLineStatus.Confirmed, OrderLineStatus.NeedsAttention)]
    [InlineData(OrderStatus.Paid, OrderLineStatus.Rejected, OrderLineStatus.AwaitingPartner)]
    [InlineData(OrderStatus.Paid, OrderLineStatus.AwaitingPartner)]
    public void BR_ORD_02_a_paid_order_takes_its_status_from_its_lines(OrderStatus expected, params OrderLineStatus[] lines)
    {
        var derived = OrderStatusDerivation.Derive(OrderStatus.Paid, lines);

        derived.Should().Be(expected);
        OrderTransitions.Order.MoveIfChanged(OrderStatus.Paid, derived).Should().Be(expected, "the documented call applies any derived status");
    }

    [Theory]
    [Trait("BR", "BR-ORD-02")]
    [InlineData(OrderStatus.Paid, OrderLineStatus.AwaitingPartner)]
    [InlineData(OrderStatus.Completed, OrderLineStatus.Confirmed)]
    [InlineData(OrderStatus.PartiallyRefunded, OrderLineStatus.Rejected, OrderLineStatus.Confirmed)]
    [InlineData(OrderStatus.AwaitingPayment, OrderLineStatus.Pending)]
    public void BR_ORD_02_re_deriving_an_unchanged_status_is_a_no_op(OrderStatus current, params OrderLineStatus[] lines)
    {
        var derived = OrderStatusDerivation.Derive(current, lines);

        derived.Should().Be(current);
        OrderTransitions.Order.MoveIfChanged(current, derived).Should().Be(current);
    }

    [Theory]
    [Trait("BR", "BR-ORD-02")]
    [InlineData(OrderStatus.AwaitingPayment)]
    [InlineData(OrderStatus.PaymentProcessing)]
    [InlineData(OrderStatus.Expired)]
    public void BR_ORD_02_before_payment_the_lines_decide_nothing(OrderStatus current) =>
        OrderStatusDerivation.Derive(current, [OrderLineStatus.Pending, OrderLineStatus.Pending]).Should().Be(current);

    [Theory]
    [Trait("BR", "BR-ORD-02")]
    [InlineData(OrderStatus.Completed, OrderLineStatus.Confirmed)]
    [InlineData(OrderStatus.PartiallyRefunded, OrderLineStatus.Rejected, OrderLineStatus.Confirmed)]
    [InlineData(OrderStatus.Refunded, OrderLineStatus.Rejected)]
    public void BR_ORD_02_a_final_order_stays_where_its_lines_put_it(OrderStatus current, params OrderLineStatus[] lines) =>
        OrderStatusDerivation.Derive(current, lines).Should().Be(current);

    [Fact]
    public void A_paid_order_without_lines_is_rejected() =>
        FluentActions.Invoking(() => OrderStatusDerivation.Derive(OrderStatus.Paid, []))
            .Should().Throw<ArgumentException>();
}
