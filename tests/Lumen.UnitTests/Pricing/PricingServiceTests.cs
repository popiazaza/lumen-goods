using System.ComponentModel.DataAnnotations;
using Lumen.Domain.Catalog;
using Lumen.Domain.Pricing;

namespace Lumen.UnitTests.Pricing;

public sealed class PricingServiceTests
{
    private readonly PricingService _pricing = new(new PricingOptions());

    [Fact]
    [Trait("BR", "BR-MONEY-02")]
    public void BR_MONEY_02_tax_is_rounded_per_line_and_summed()
    {
        // Scenario 1: lines $16.22 and 2 × $49.90.
        var quote = _pricing.Price([Line(ProductSources.Kestrel, 16.22m), Line(ProductSources.Harbor, 49.90m, 2)], ShippingMethod.Standard);

        quote.Lines.Select(l => l.Tax).Should().Equal(1.30m, 7.98m);
        quote.TaxTotal.Should().Be(9.28m);
    }

    [Fact]
    [Trait("BR", "BR-MONEY-02")]
    public void BR_MONEY_02_three_small_lines_are_taxed_per_line_not_on_the_subtotal()
    {
        // Scenario 2: 3 × $0.08 = $0.24; 8 % of the $3.18 subtotal would round to $0.25.
        var quote = _pricing.Price(
            [Line(ProductSources.Lumen, 1.06m), Line(ProductSources.Harbor, 1.06m), Line(ProductSources.Kestrel, 1.06m)],
            ShippingMethod.Standard);

        quote.Lines.Should().AllSatisfy(l => l.Tax.Should().Be(0.08m));
        quote.TaxTotal.Should().Be(0.24m);
    }

    [Theory]
    [Trait("BR", "BR-MONEY-02")]
    [InlineData(ShippingMethod.Standard)]
    [InlineData(ShippingMethod.Express)]
    public void BR_MONEY_02_shipping_is_never_taxed(ShippingMethod method)
    {
        var quote = _pricing.Price([Line(ProductSources.Lumen, 10.00m)], method);

        quote.TaxTotal.Should().Be(0.80m);
        quote.GrandTotal.Should().Be(10.00m + 0.80m + quote.ShippingTotal);
    }

    [Fact]
    [Trait("BR", "BR-MONEY-02")]
    public void BR_MONEY_02_a_line_subtotal_is_unit_price_times_quantity_with_no_extra_rounding()
    {
        var line = _pricing.Price([Line(ProductSources.Kestrel, 16.22m, 3)], ShippingMethod.Standard).Lines.Single();

        line.Subtotal.Should().Be(48.66m);
        line.Tax.Should().Be(3.89m); // 3.8928
        line.Total.Should().Be(52.55m);
    }

    [Fact]
    [Trait("BR", "BR-MONEY-02")]
    public void BR_MONEY_02_tax_rounds_half_away_from_zero()
    {
        // 5 % of $0.10 is exactly $0.005: half away from zero gives $0.01, banker's rounding would give $0.00.
        var pricing = new PricingService(new PricingOptions { TaxRate = 0.05m });

        pricing.Price([Line(ProductSources.Lumen, 0.10m)], ShippingMethod.Standard).TaxTotal.Should().Be(0.01m);
    }

    [Fact]
    [Trait("BR", "BR-MONEY-02")]
    public void BR_MONEY_02_the_grand_total_is_subtotal_plus_shipping_plus_tax()
    {
        // The review total from BR-CHK-01: Kestrel $16.22 + Harbor $49.90 = $81.39.
        var quote = _pricing.Price([Line(ProductSources.Kestrel, 16.22m), Line(ProductSources.Harbor, 49.90m)], ShippingMethod.Standard);

        quote.Subtotal.Should().Be(66.12m);
        quote.ShippingTotal.Should().Be(9.98m);
        quote.TaxTotal.Should().Be(5.29m);
        quote.GrandTotal.Should().Be(81.39m);
    }

    [Fact]
    [Trait("BR", "BR-MONEY-02")]
    public void BR_MONEY_02_the_tax_rate_is_configurable() =>
        new PricingService(new PricingOptions { TaxRate = 0.10m })
            .Price([Line(ProductSources.Kestrel, 16.22m)], ShippingMethod.Standard).TaxTotal.Should().Be(1.62m);

    [Fact]
    [Trait("BR", "BR-SHIP-01")]
    public void BR_SHIP_01_each_seller_ships_separately_on_standard()
    {
        // Scenario 1: Lumen, Harbor and Kestrel lines on Standard → $14.97.
        var quote = _pricing.Price(
            [Line(ProductSources.Kestrel, 16.22m), Line(ProductSources.Lumen, 25.00m), Line(ProductSources.Harbor, 49.90m)],
            ShippingMethod.Standard);

        quote.Shipments.Should().Equal(
            new Shipment(ProductSources.Lumen, ShippingMethod.Standard, 4.99m),
            new Shipment(ProductSources.Harbor, ShippingMethod.Standard, 4.99m),
            new Shipment(ProductSources.Kestrel, ShippingMethod.Standard, 4.99m));
        quote.ShippingTotal.Should().Be(14.97m);
    }

    [Fact]
    [Trait("BR", "BR-SHIP-01")]
    public void BR_SHIP_01_express_applies_only_to_the_lumen_shipment()
    {
        // Scenario 2: Lumen lines on Express and a Kestrel line → $12.99 + $4.99 = $17.98.
        var quote = _pricing.Price(
            [Line(ProductSources.Lumen, 25.00m), Line(ProductSources.Lumen, 10.00m, 2), Line(ProductSources.Kestrel, 16.22m)],
            ShippingMethod.Express);

        quote.Shipments.Should().Equal(
            new Shipment(ProductSources.Lumen, ShippingMethod.Express, 12.99m),
            new Shipment(ProductSources.Kestrel, ShippingMethod.Standard, 4.99m));
        quote.ShippingTotal.Should().Be(17.98m);
    }

    [Fact]
    [Trait("BR", "BR-SHIP-01")]
    public void BR_SHIP_01_express_is_not_offered_for_a_cart_with_only_partner_items()
    {
        // Scenario 3.
        PricingLine[] partnerOnly = [Line(ProductSources.Harbor, 49.90m), Line(ProductSources.Kestrel, 16.22m)];

        _pricing.ShippingMethodsFor(partnerOnly).Should().Equal(ShippingMethod.Standard);
        FluentActions.Invoking(() => _pricing.Price(partnerOnly, ShippingMethod.Express))
            .Should().Throw<ShippingMethodNotAvailableException>()
            .WithMessage("Express shipping is only offered for Lumen Goods' own items, and this cart has none.");
    }

    [Fact]
    [Trait("BR", "BR-SHIP-01")]
    public void BR_SHIP_01_express_is_offered_when_the_cart_has_own_items() =>
        _pricing.ShippingMethodsFor([Line(ProductSources.Harbor, 49.90m), Line(ProductSources.Lumen, 25.00m)])
            .Should().Equal(ShippingMethod.Standard, ShippingMethod.Express);

    [Fact]
    [Trait("BR", "BR-SHIP-01")]
    public void BR_SHIP_01_several_lines_from_one_seller_are_one_shipment() =>
        _pricing.Price([Line(ProductSources.Harbor, 49.90m), Line(ProductSources.Harbor, 20.00m, 3)], ShippingMethod.Standard)
            .Shipments.Should().ContainSingle().Which.Fee.Should().Be(4.99m);

    [Fact]
    [Trait("BR", "BR-SHIP-01")]
    public void BR_SHIP_01_shipping_fees_are_configurable()
    {
        var pricing = new PricingService(new PricingOptions { StandardShippingFee = 5.50m, ExpressShippingFee = 15.00m });

        pricing.Price([Line(ProductSources.Lumen, 10.00m), Line(ProductSources.Harbor, 10.00m)], ShippingMethod.Express)
            .ShippingTotal.Should().Be(20.50m);
    }

    [Fact]
    public void An_empty_cart_costs_nothing_and_has_no_shipments()
    {
        var quote = _pricing.Price([], ShippingMethod.Standard);

        quote.Shipments.Should().BeEmpty();
        quote.GrandTotal.Should().Be(0m);
    }

    [Theory]
    [InlineData("lumen", "10.00", 0)]  // quantity below 1
    [InlineData("acme", "10.00", 1)]   // unknown seller
    [InlineData("lumen", "10.005", 1)] // not whole cents
    [InlineData("lumen", "-1.00", 1)]  // negative price
    public void Invalid_lines_are_rejected(string source, string unitPrice, int quantity) =>
        FluentActions.Invoking(() => _pricing.Price(
                [Line(source, decimal.Parse(unitPrice, System.Globalization.CultureInfo.InvariantCulture), quantity)], ShippingMethod.Standard))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void Invalid_options_are_rejected_when_the_service_is_built() =>
        FluentActions.Invoking(() => new PricingService(new PricingOptions { TaxRate = -0.08m }))
            .Should().Throw<ValidationException>();

    private static PricingLine Line(string source, decimal unitPrice, int quantity = 1) => new(Guid.NewGuid(), source, unitPrice, quantity);
}
