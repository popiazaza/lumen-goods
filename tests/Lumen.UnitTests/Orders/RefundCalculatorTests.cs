using Lumen.Domain.Catalog;
using Lumen.Domain.Orders;
using Lumen.Domain.Pricing;

namespace Lumen.UnitTests.Orders;

/// <summary>Orders are priced with the real <see cref="PricingService"/>, so these tests also show the two rules fit together.</summary>
public sealed class RefundCalculatorTests
{
    private static readonly PricingService Pricing = new(new PricingOptions());

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public void BR_ORD_03_a_rejected_kestrel_line_refunds_its_subtotal_tax_and_the_kestrel_shipping()
    {
        // Scenario 1: a Lumen line and one Kestrel line 2 × $16.22 (tax $2.60); Kestrel rejects it OUT_OF_STOCK.
        var order = PaidOrder(
            (ProductSources.Lumen, 25.00m, 1, OrderLineStatus.Confirmed),
            (ProductSources.Kestrel, 16.22m, 2, OrderLineStatus.AwaitingPartner));
        var kestrel = order.Lines[1];

        var refund = RefundCalculator.Calculate(order, [kestrel.LineId]);

        refund.Lines.Should().ContainSingle().Which.Should().Be(new LineRefund(kestrel.LineId, ProductSources.Kestrel, 32.44m, 2.60m));
        refund.Shipping.Should().Equal(new ShippingRefund(ProductSources.Kestrel, 4.99m));
        refund.Total.Should().Be(40.03m);
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public void BR_ORD_03_one_of_two_harbor_lines_rejected_keeps_the_harbor_shipping_charged()
    {
        // Scenario 2.
        var order = PaidOrder(
            (ProductSources.Harbor, 49.90m, 1, OrderLineStatus.AwaitingPartner),
            (ProductSources.Harbor, 20.00m, 1, OrderLineStatus.AwaitingPartner));

        var refund = RefundCalculator.Calculate(order, [order.Lines[0].LineId]);

        refund.Total.Should().Be(49.90m + 3.99m);
        refund.Shipping.Should().BeEmpty();
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public void BR_ORD_03_all_partner_lines_rejected_at_once_refunds_exactly_the_amount_charged()
    {
        // Scenario 3: Harbor $49.90 + Kestrel $16.22 = $81.39 charged.
        var order = PaidOrder(
            (ProductSources.Harbor, 49.90m, 1, OrderLineStatus.AwaitingPartner),
            (ProductSources.Kestrel, 16.22m, 1, OrderLineStatus.AwaitingPartner));

        var refund = RefundCalculator.Calculate(order, [.. order.Lines.Select(l => l.LineId)]);

        order.AmountCharged.Should().Be(81.39m);
        refund.Total.Should().Be(order.AmountCharged);
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public void BR_ORD_03_partner_lines_rejected_one_by_one_add_up_to_the_amount_charged()
    {
        var order = PaidOrder(
            (ProductSources.Harbor, 49.90m, 1, OrderLineStatus.AwaitingPartner),
            (ProductSources.Kestrel, 16.22m, 1, OrderLineStatus.AwaitingPartner));

        var first = RefundCalculator.Calculate(order, [order.Lines[0].LineId]);
        var afterFirst = Recorded(order, first);
        var second = RefundCalculator.Calculate(afterFirst, [order.Lines[1].LineId]);

        first.Total.Should().Be(49.90m + 3.99m + 4.99m);
        second.Total.Should().Be(16.22m + 1.30m + 4.99m);
        (first.Total + second.Total).Should().Be(order.AmountCharged);
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public void BR_ORD_03_the_shipping_fee_is_refunded_when_the_last_line_of_the_shipment_is_rejected()
    {
        var order = PaidOrder(
            (ProductSources.Kestrel, 16.22m, 1, OrderLineStatus.AwaitingPartner),
            (ProductSources.Kestrel, 10.00m, 1, OrderLineStatus.AwaitingPartner));

        var first = RefundCalculator.Calculate(order, [order.Lines[0].LineId]);
        var second = RefundCalculator.Calculate(Recorded(order, first), [order.Lines[1].LineId]);

        first.Shipping.Should().BeEmpty("the other Kestrel line may still ship");
        second.Shipping.Should().Equal(new ShippingRefund(ProductSources.Kestrel, 4.99m));
        second.Total.Should().Be(10.00m + 0.80m + 4.99m);
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public void BR_ORD_03_a_rejection_seen_again_refunds_nothing()
    {
        var order = PaidOrder(
            (ProductSources.Lumen, 25.00m, 1, OrderLineStatus.Confirmed),
            (ProductSources.Kestrel, 16.22m, 2, OrderLineStatus.AwaitingPartner));
        var kestrelId = order.Lines[1].LineId;

        var first = RefundCalculator.Calculate(order, [kestrelId]);
        var again = RefundCalculator.Calculate(Recorded(order, first), [kestrelId]);

        first.Total.Should().Be(40.03m);
        again.Total.Should().Be(0m);
        again.Lines.Should().BeEmpty();
        again.Shipping.Should().BeEmpty();
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public void BR_ORD_03_the_same_line_twice_in_one_call_is_refunded_once()
    {
        var order = PaidOrder((ProductSources.Harbor, 49.90m, 1, OrderLineStatus.AwaitingPartner));
        var id = order.Lines[0].LineId;

        RefundCalculator.Calculate(order, [id, id]).Total.Should().Be(49.90m + 3.99m + 4.99m);
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public void BR_ORD_03_a_shipping_fee_already_refunded_is_not_refunded_again()
    {
        var priced = PaidOrder(
            (ProductSources.Kestrel, 16.22m, 1, OrderLineStatus.Rejected),
            (ProductSources.Kestrel, 10.00m, 1, OrderLineStatus.AwaitingPartner));
        var order = priced with { Shipments = [.. priced.Shipments.Select(s => s with { FeeRefunded = true })] };

        var refund = RefundCalculator.Calculate(order, [order.Lines[1].LineId]);

        refund.Shipping.Should().BeEmpty();
        refund.Total.Should().Be(10.80m);
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public void BR_ORD_03_a_refund_that_would_exceed_the_amount_charged_means_the_snapshot_is_wrong_and_throws()
    {
        // $58.88 is owed, but the snapshot says only $10.00 is left: paying a trimmed amount would hide the bad data.
        var priced = PaidOrder((ProductSources.Harbor, 49.90m, 1, OrderLineStatus.AwaitingPartner));
        var order = priced with { AmountRefunded = priced.AmountCharged - 10.00m };

        FluentActions.Invoking(() => RefundCalculator.Calculate(order, [order.Lines[0].LineId]))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("Inconsistent order snapshot: a refund of 58.88 would exceed the 10.00 left to refund (58.88 charged, 48.88 already refunded).");
    }

    [Theory]
    [Trait("BR", "BR-ORD-03")]
    [InlineData("-0.01")]
    [InlineData("58.89")]
    public void BR_ORD_03_a_snapshot_refunding_less_than_nothing_or_more_than_was_charged_is_rejected(string amountRefunded)
    {
        var priced = PaidOrder((ProductSources.Harbor, 49.90m, 1, OrderLineStatus.AwaitingPartner));
        var order = priced with { AmountRefunded = decimal.Parse(amountRefunded, System.Globalization.CultureInfo.InvariantCulture) };

        FluentActions.Invoking(() => RefundCalculator.Calculate(order, []))
            .Should().Throw<InvalidOperationException>().WithMessage("Inconsistent order snapshot:*");
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public void BR_ORD_03_a_fully_refunded_order_seeing_its_rejection_again_refunds_nothing()
    {
        var priced = PaidOrder((ProductSources.Harbor, 49.90m, 1, OrderLineStatus.AwaitingPartner));
        var refunded = Recorded(priced, RefundCalculator.Calculate(priced, [priced.Lines[0].LineId]));

        refunded.AmountRefunded.Should().Be(refunded.AmountCharged);
        RefundCalculator.Calculate(refunded, [priced.Lines[0].LineId]).Total.Should().Be(0m);
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public void BR_ORD_03_the_total_is_always_the_sum_of_the_items()
    {
        var order = PaidOrder(
            (ProductSources.Harbor, 49.90m, 1, OrderLineStatus.AwaitingPartner),
            (ProductSources.Kestrel, 16.22m, 2, OrderLineStatus.AwaitingPartner),
            (ProductSources.Kestrel, 10.00m, 1, OrderLineStatus.Rejected));

        var refund = RefundCalculator.Calculate(order, [.. order.Lines.Select(l => l.LineId)]);

        refund.Total.Should().Be(refund.Lines.Sum(l => l.Amount) + refund.Shipping.Sum(s => s.Fee));
    }

    [Fact]
    [Trait("BR", "BR-ORD-04")]
    public void BR_ORD_04_a_line_needing_attention_that_the_admin_rejects_is_refunded()
    {
        var order = PaidOrder((ProductSources.Kestrel, 16.22m, 1, OrderLineStatus.NeedsAttention));

        RefundCalculator.Calculate(order, [order.Lines[0].LineId]).Total.Should().Be(16.22m + 1.30m + 4.99m);
    }

    [Theory]
    [Trait("BR", "BR-ORD-03")]
    [InlineData(OrderLineStatus.Confirmed)]
    [InlineData(OrderLineStatus.Pending)]
    public void BR_ORD_03_a_line_that_cannot_be_rejected_from_its_status_is_an_error(OrderLineStatus status)
    {
        var order = PaidOrder((ProductSources.Harbor, 49.90m, 1, status));

        FluentActions.Invoking(() => RefundCalculator.Calculate(order, [order.Lines[0].LineId]))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_line_id_not_on_the_order_is_an_error()
    {
        var order = PaidOrder((ProductSources.Harbor, 49.90m, 1, OrderLineStatus.AwaitingPartner));

        FluentActions.Invoking(() => RefundCalculator.Calculate(order, [Guid.NewGuid()]))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_rejected_lines_means_no_refund() =>
        RefundCalculator.Calculate(PaidOrder((ProductSources.Harbor, 49.90m, 1, OrderLineStatus.AwaitingPartner)), [])
            .Should().BeEquivalentTo(new { Total = 0m, Lines = Array.Empty<LineRefund>(), Shipping = Array.Empty<ShippingRefund>() });

    private static PaidOrderSnapshot PaidOrder(params (string Source, decimal UnitPrice, int Quantity, OrderLineStatus Status)[] lines)
    {
        var quote = Pricing.Price([.. lines.Select(l => new PricingLine(Guid.NewGuid(), l.Source, l.UnitPrice, l.Quantity))], ShippingMethod.Standard);
        return new PaidOrderSnapshot(
            [.. quote.Lines.Select((l, i) => new PaidOrderLine(Guid.NewGuid(), l.Source, l.Subtotal, l.Tax, lines[i].Status))],
            [.. quote.Shipments.Select(s => new PaidShipment(s.Source, s.Fee, FeeRefunded: false))],
            AmountCharged: quote.GrandTotal,
            AmountRefunded: 0m);
    }

    /// <summary>What the API does after paying out a refund: lines Rejected, fees marked refunded, amount added.</summary>
    private static PaidOrderSnapshot Recorded(PaidOrderSnapshot order, RefundBreakdown refund)
    {
        var refundedLines = refund.Lines.Select(l => l.LineId).ToHashSet();
        var refundedFees = refund.Shipping.Select(s => s.Source).ToHashSet();
        return order with
        {
            Lines = [.. order.Lines.Select(l => refundedLines.Contains(l.LineId) ? l with { Status = OrderLineStatus.Rejected } : l)],
            Shipments = [.. order.Shipments.Select(s => refundedFees.Contains(s.Source) ? s with { FeeRefunded = true } : s)],
            AmountRefunded = order.AmountRefunded + refund.Total,
        };
    }
}
