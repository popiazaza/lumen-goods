using CsCheck;
using Lumen.Api.Partners;
using Lumen.Domain.Catalog;
using Lumen.Domain.Money;
using Lumen.Domain.Orders;
using Lumen.Domain.Pricing;
using Microsoft.Extensions.Configuration;

namespace Lumen.UnitTests.Money;

/// <summary>
/// Property-based tests (CsCheck): the money rules hold for thousands of random carts, not only for the worked examples
/// in <c>docs/BUSINESS-RULES.md</c>. A cart is 1-10 lines from Lumen, Harbor and Kestrel at 0.01-2,000.00 (Kestrel's in
/// EUR, converted at the shipped rate), 1-10 units each, priced by the real <see cref="PricingService"/> with the shipped
/// tax rate and fees, then refunded by the real <see cref="RefundCalculator"/> for random sets of rejected partner lines.
/// A failure shrinks to the smallest failing cart and prints a seed that replays it.
/// </summary>
public sealed class MoneyPropertyTests
{
    private const int Iterations = 5_000;

    private static readonly IConfiguration Shipped = new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(RepoRoot(), "src", "Lumen.Api", "appsettings.json"))
        .Build();

    private static readonly decimal EurToUsd = Shipped.GetSection(FxOptions.SectionName).Get<FxOptions>()!.EurToUsd;

    private static readonly PricingOptions Options = Shipped.GetSection(PricingOptions.SectionName).Get<PricingOptions>()!;

    private static readonly PricingService Pricing = new(Options);

    /// <summary>0.01 to 2,000.00 in whole cents.</summary>
    private static readonly Gen<decimal> GenPrice = Gen.Int[1, 200_000].Select(cents => cents / 100m);

    private static readonly Gen<CartLine> GenLine = Gen.Select(
        Gen.OneOf(ProductSources.Lumen, ProductSources.Harbor, ProductSources.Kestrel), GenPrice, Gen.Int[1, 10],
        (source, price, quantity) => new CartLine(source, price, quantity));

    private static readonly Gen<CartLine> GenPartnerLine = Gen.Select(
        Gen.OneOf(ProductSources.Harbor, ProductSources.Kestrel), GenPrice, Gen.Int[1, 10],
        (source, price, quantity) => new CartLine(source, price, quantity));

    /// <summary>Express is asked for only when the cart has Lumen's own items, as the checkout offers it (BR-SHIP-01).</summary>
    private static readonly Gen<Cart> GenCart = Gen.Select(GenLine.Array[1, 10], Gen.Bool, (lines, express) =>
        new Cart(lines, express && lines.Any(l => l.Source == ProductSources.Lumen) ? ShippingMethod.Express : ShippingMethod.Standard));

    /// <summary>A paid cart and 1-4 rejection rounds, each a random subset of its lines (a line may be rejected again).</summary>
    private static readonly Gen<(Cart Cart, bool[][] Rounds)> GenRejections =
        from cart in GenCart
        from rounds in Gen.Bool.Array[cart.Lines.Length].Array[1, 4]
        select (cart, rounds);

    [Fact]
    [Trait("BR", "BR-MONEY-01")]
    public void BR_MONEY_01_any_eur_price_converts_to_whole_cents_within_half_a_cent_and_half_rounds_up() =>
        GenPrice.Sample(eur =>
        {
            var exact = eur * EurToUsd;
            var usd = MoneyMath.ConvertToUsd(eur, EurToUsd);

            IsWholeCents(usd).Should().BeTrue();
            Math.Abs(usd - exact).Should().BeLessThanOrEqualTo(0.005m);
            if (Math.Abs(usd - exact) == 0.005m)
            {
                usd.Should().BeGreaterThan(exact, "a half cent rounds away from zero, never to even");
            }
        }, iter: Iterations);

    [Fact]
    [Trait("BR", "BR-MONEY-02")]
    [Trait("BR", "BR-SHIP-01")]
    public void BR_MONEY_02_any_cart_total_is_items_plus_shipping_plus_tax_in_whole_cents() =>
        GenCart.Sample(cart =>
        {
            var quote = Price(cart);

            // What the rules say, worked out from the cart itself: items at unit price x quantity, one fee per seller
            // shipment (Express only for Lumen's), tax rounded per line.
            var items = cart.Lines.Sum(l => l.UnitPriceUsd * l.Quantity);
            var shipping = cart.Lines.Select(l => l.Source).Distinct().Sum(source =>
                source == Options.ExpressSource && cart.Method == ShippingMethod.Express ? Options.ExpressShippingFee : Options.StandardShippingFee);
            var tax = cart.Lines.Sum(l => MoneyMath.RoundToCents(l.UnitPriceUsd * l.Quantity * Options.TaxRate));

            (quote.Subtotal, quote.ShippingTotal, quote.TaxTotal, quote.GrandTotal).Should().Be((items, shipping, tax, items + shipping + tax));
            quote.Shipments.Select(s => s.Source).Should().OnlyHaveUniqueItems();
            quote.Lines.SelectMany(l => new[] { l.UnitPrice, l.Subtotal, l.Tax })
                .Concat(quote.Shipments.Select(s => s.Fee))
                .Concat([quote.Subtotal, quote.ShippingTotal, quote.TaxTotal, quote.GrandTotal])
                .Should().OnlyContain(amount => amount >= 0 && IsWholeCents(amount));
        }, iter: Iterations);

    [Fact]
    [Trait("BR", "BR-MONEY-02")]
    public void BR_MONEY_02_any_cart_tax_is_the_sum_of_line_taxes_each_within_half_a_cent_and_never_on_shipping() =>
        GenCart.Sample(cart =>
        {
            var quote = Price(cart);

            quote.TaxTotal.Should().Be(quote.Lines.Sum(l => l.Tax));
            foreach (var line in quote.Lines)
            {
                var exact = line.Subtotal * Options.TaxRate;
                Math.Abs(line.Tax - exact).Should().BeLessThanOrEqualTo(0.005m);
                if (Math.Abs(line.Tax - exact) == 0.005m)
                {
                    line.Tax.Should().BeGreaterThan(exact, "a half cent rounds away from zero, never to even");
                }
            }

            // Shipping is not taxed: Standard and Express give the same tax.
            Price(cart with { Method = ShippingMethod.Standard }).TaxTotal.Should().Be(quote.TaxTotal);
        }, iter: Iterations);

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public void BR_ORD_03_any_sequence_of_rejections_refunds_at_most_the_amount_charged_and_each_line_once() =>
        GenRejections.Sample(sample =>
        {
            var quote = Price(sample.Cart);
            var order = PaidOrder(quote);
            var refunds = new List<RefundBreakdown>();

            foreach (var round in sample.Rounds)
            {
                var refund = RefundCalculator.Calculate(order, RejectedPartnerLines(order, round));
                refund.Total.Should().Be(refund.Lines.Sum(l => l.Amount) + refund.Shipping.Sum(s => s.Fee));
                refund.Total.Should().BeGreaterThanOrEqualTo(0m);
                refunds.Add(refund);
                order = Recorded(order, refund);
            }

            refunds.Sum(r => r.Total).Should().BeLessThanOrEqualTo(quote.GrandTotal);
            refunds.SelectMany(r => r.Lines).Select(l => l.LineId).Should().OnlyHaveUniqueItems("a line is refunded once");
            refunds.SelectMany(r => r.Shipping).Select(s => s.Source).Should().OnlyHaveUniqueItems("a shipment fee is refunded once");

            // Exactly what was not supplied: each rejected line with its own tax, plus the fee of every shipment left empty.
            var rejected = order.Lines.Where(l => l.Status == OrderLineStatus.Rejected).ToList();
            var emptied = order.Shipments.Where(s => order.Lines.Where(l => l.Source == s.Source).All(l => l.Status == OrderLineStatus.Rejected));
            refunds.Sum(r => r.Total).Should().Be(rejected.Sum(l => l.Subtotal + l.Tax) + emptied.Sum(s => s.Fee));
        }, iter: Iterations);

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public void BR_ORD_03_rejecting_every_line_in_any_order_refunds_exactly_the_amount_charged() =>
        (from lines in GenPartnerLine.Array[1, 10]
         from roundOfLine in Gen.Int[0, 3].Array[lines.Length]
         select (Cart: new Cart(lines, ShippingMethod.Standard), RoundOfLine: roundOfLine))
        .Sample(sample =>
        {
            var quote = Price(sample.Cart);
            var order = PaidOrder(quote);
            var refunded = 0m;

            // Every line is rejected in one of up to four rounds (all at once when they share a round).
            for (var round = 0; round <= 3; round++)
            {
                var ids = order.Lines.Where((_, i) => sample.RoundOfLine[i] == round).Select(l => l.LineId).ToList();
                var refund = RefundCalculator.Calculate(order, ids);
                refunded += refund.Total;
                order = Recorded(order, refund);
            }

            refunded.Should().Be(quote.GrandTotal);
            order.AmountRefunded.Should().Be(order.AmountCharged);
        }, iter: Iterations);

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public void BR_ORD_03_a_shipment_fee_is_refunded_only_once_every_line_of_that_seller_is_rejected() =>
        GenRejections.Sample(sample =>
        {
            var order = PaidOrder(Price(sample.Cart));
            var feesRefunded = new HashSet<string>();

            foreach (var round in sample.Rounds)
            {
                var refund = RefundCalculator.Calculate(order, RejectedPartnerLines(order, round));
                order = Recorded(order, refund);
                feesRefunded.UnionWith(refund.Shipping.Select(s => s.Source));

                var sellersFullyRejected = order.Shipments
                    .Where(s => s.Fee > 0 && order.Lines.Where(l => l.Source == s.Source).All(l => l.Status == OrderLineStatus.Rejected))
                    .Select(s => s.Source);
                feesRefunded.Should().BeEquivalentTo(sellersFullyRejected, "a seller's fee is refunded once all of its lines are rejected, not before");
            }

            feesRefunded.Should().NotContain(ProductSources.Lumen, "own lines are confirmed on payment and never rejected");
        }, iter: Iterations);

    private static PriceQuote Price(Cart cart) =>
        Pricing.Price([.. cart.Lines.Select((l, i) => new PricingLine(LineId(i), l.Source, l.UnitPriceUsd, l.Quantity))], cart.Method);

    /// <summary>The order once paid: own lines confirmed, partner lines waiting for their partner (BR-ORD-01).</summary>
    private static PaidOrderSnapshot PaidOrder(PriceQuote quote) => new(
        [.. quote.Lines.Select(l => new PaidOrderLine(l.ProductId, l.Source, l.Subtotal, l.Tax,
            l.Source == ProductSources.Lumen ? OrderLineStatus.Confirmed : OrderLineStatus.AwaitingPartner))],
        [.. quote.Shipments.Select(s => new PaidShipment(s.Source, s.Fee, FeeRefunded: false))],
        AmountCharged: quote.GrandTotal,
        AmountRefunded: 0m);

    /// <summary>Only partner lines can be rejected; an own line in the round is left out.</summary>
    private static List<Guid> RejectedPartnerLines(PaidOrderSnapshot order, bool[] round) =>
        [.. order.Lines.Where((l, i) => round[i] && l.Source != ProductSources.Lumen).Select(l => l.LineId)];

    /// <summary>What the API records after a refund: its lines Rejected, its fees marked refunded, the amount added.</summary>
    private static PaidOrderSnapshot Recorded(PaidOrderSnapshot order, RefundBreakdown refund)
    {
        var lines = refund.Lines.Select(l => l.LineId).ToHashSet();
        var fees = refund.Shipping.Select(s => s.Source).ToHashSet();
        return order with
        {
            Lines = [.. order.Lines.Select(l => lines.Contains(l.LineId) ? l with { Status = OrderLineStatus.Rejected } : l)],
            Shipments = [.. order.Shipments.Select(s => fees.Contains(s.Source) ? s with { FeeRefunded = true } : s)],
            AmountRefunded = order.AmountRefunded + refund.Total,
        };
    }

    /// <summary>Stable ids, so a printed failing case replays exactly.</summary>
    private static Guid LineId(int index) => new(index + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static bool IsWholeCents(decimal amount) => MoneyMath.RoundToCents(amount) == amount;

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LumenGoods.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (LumenGoods.slnx).");
    }

    /// <param name="SourcePrice">USD for Lumen and Harbor; EUR for Kestrel, converted per unit at the shipped rate (BR-MONEY-01).</param>
    private sealed record CartLine(string Source, decimal SourcePrice, int Quantity)
    {
        public decimal UnitPriceUsd => Source == ProductSources.Kestrel ? MoneyMath.ConvertToUsd(SourcePrice, EurToUsd) : SourcePrice;

        public override string ToString() =>
            $"{Source} {Quantity} x {SourcePrice:0.00} {(Source == ProductSources.Kestrel ? "EUR" : "USD")}";
    }

    private sealed record Cart(CartLine[] Lines, ShippingMethod Method)
    {
        public override string ToString() => $"[{string.Join("; ", Lines.Select(l => l.ToString()))}] {Method}";
    }
}
