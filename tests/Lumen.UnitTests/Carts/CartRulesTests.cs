using Lumen.Api.Carts;
using Lumen.Api.Catalog;
using Lumen.Domain.Catalog;

namespace Lumen.UnitTests.Carts;

/// <summary>The cart's rules on their own: notices (BR-CART-02), limits (BR-CART-03) and merge caps (BR-CART-01).</summary>
public sealed class CartRulesTests
{
    private static readonly AvailabilityPolicy Policy = new();
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    private static Product Partner(decimal? price = 49.90m, int? quantity = 20, AvailabilityStatus whenUnknown = AvailabilityStatus.InStock,
        bool active = true, bool listed = true) => new()
        {
            Source = ProductSources.Harbor,
            SourceProductId = "HSC-1",
            Name = "Canvas Tote",
            Category = ProductCategories.Bags,
            PriceUsd = price,
            Quantity = quantity,
            StatusWhenQuantityUnknown = whenUnknown,
            IsActive = active,
            IsListed = listed,
        };

    private static CartLine Line(int quantity = 1, decimal seenPrice = 49.90m, AvailabilityStatus seen = AvailabilityStatus.InStock) => new()
    {
        ProductId = Guid.NewGuid(),
        Quantity = quantity,
        UnitPriceSeenUsd = seenPrice,
        AvailabilitySeen = seen,
        SellableSeen = true,
        AddedUtc = Now,
    };

    [Fact]
    [Trait("BR", "BR-CART-02")]
    public void BR_CART_02_an_unchanged_product_has_no_notices()
    {
        var state = CartRules.Evaluate(Line(2), Partner(), Policy);

        state.Notices.Should().Be(CartLineNoticesDto.None);
        state.BlocksCheckout.Should().BeFalse();
        state.MaxQuantity.Should().Be(10);
        state.UnitPrice.Should().Be(49.90m);
    }

    [Theory]
    [Trait("BR", "BR-CART-02")]
    [InlineData(54.90)]
    [InlineData(44.90)]
    public void BR_CART_02_a_price_up_or_down_shows_from_and_to(double priceNow)
    {
        var now = (decimal)priceNow;
        var state = CartRules.Evaluate(Line(seenPrice: 49.90m), Partner(price: now), Policy);

        state.Notices.PriceChanged.Should().Be(new PriceChangedNoticeDto(49.90m, now));
        state.BlocksCheckout.Should().BeFalse();
    }

    [Theory]
    [Trait("BR", "BR-CART-02")]
    [InlineData(AvailabilityStatus.InStock, 3, true)]
    [InlineData(AvailabilityStatus.LowStock, 0, true)]
    [InlineData(AvailabilityStatus.LowStock, 4, false)]
    [InlineData(AvailabilityStatus.OutOfStock, 20, false)]
    public void BR_CART_02_only_a_worse_availability_shows_a_notice(AvailabilityStatus seen, int quantityNow, bool expected)
    {
        var state = CartRules.Evaluate(Line(seen: seen), Partner(quantity: quantityNow), Policy);

        (state.Notices.AvailabilityChanged is not null).Should().Be(expected);
    }

    [Theory]
    [Trait("BR", "BR-CART-03")]
    [InlineData(false, true, 49.90)]
    [InlineData(true, false, 49.90)]
    [InlineData(true, true, null)]
    public void BR_CART_03_a_product_that_is_not_sellable_is_no_longer_available_and_blocks_checkout(bool active, bool listed, double? price)
    {
        var product = Partner(price: (decimal?)price, active: active, listed: listed);

        var state = CartRules.Evaluate(Line(), product, Policy);

        state.Unavailable.Should().Be(CartLineUnavailableReason.NoLongerAvailable);
        state.Notices.Unavailable.Should().Be(CartLineUnavailableReason.NoLongerAvailable);
        state.BlocksCheckout.Should().BeTrue();
        state.MaxQuantity.Should().Be(0);
        CartRules.CheckRaise(product, Policy, 0, 1)!.Type.Should().Be(CartProblems.ProductUnavailableType);
    }

    [Fact]
    [Trait("BR", "BR-CART-03")]
    public void BR_CART_03_out_of_stock_blocks_checkout_and_cannot_be_added()
    {
        var product = Partner(quantity: 0);

        var state = CartRules.Evaluate(Line(2), product, Policy);

        state.Unavailable.Should().Be(CartLineUnavailableReason.OutOfStock);
        state.Notices.QuantityLimited.Should().BeNull("an unavailable line says so instead of offering a quantity");
        state.BlocksCheckout.Should().BeTrue();
        CartRules.CheckRaise(product, Policy, 0, 1)!.Extensions["reason"].Should().Be("out_of_stock");
    }

    [Fact]
    [Trait("BR", "BR-CART-03")]
    public void BR_CART_03_a_line_above_the_known_stock_shows_only_n_left_and_blocks_checkout()
    {
        var state = CartRules.Evaluate(Line(4), Partner(quantity: 2), Policy);

        state.Notices.QuantityLimited.Should().Be(new QuantityLimitedNoticeDto(4, 2));
        state.MaxQuantity.Should().Be(2);
        state.BlocksCheckout.Should().BeTrue();
    }

    [Theory]
    [Trait("BR", "BR-CART-03")]
    [InlineData(null, 0, 10, true)]
    [InlineData(null, 8, 11, false)]
    [InlineData(3, 0, 3, true)]
    [InlineData(3, 2, 4, false)]
    public void BR_CART_03_adding_respects_10_per_line_and_the_known_stock(int? stock, int inCart, int newQuantity, bool allowed)
    {
        var product = Partner(quantity: stock, whenUnknown: AvailabilityStatus.InStock);

        (CartRules.CheckRaise(product, Policy, inCart, newQuantity) is null).Should().Be(allowed);
    }

    [Theory]
    [Trait("BR", "BR-CART-01")]
    [InlineData(1, 2, null, 3, null)]
    [InlineData(5, 9, null, 10, 14)]
    [InlineData(3, 4, 6, 6, 7)]
    [InlineData(4, 1, 2, 2, 5)]
    public void BR_CART_01_merged_quantities_are_summed_and_capped_at_stock_and_10(int saved, int guest, int? stock, int expected, int? limitedFrom)
    {
        CartRules.MergedQuantity(saved, guest, Partner(quantity: stock), Policy).Should().Be((expected, limitedFrom));
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public void BR_CART_01_merging_a_product_that_cannot_be_bought_only_caps_at_10()
    {
        CartRules.MergedQuantity(6, 6, Partner(quantity: 0), Policy).Should().Be((10, 12));
        CartRules.MergedQuantity(1, 1, Partner(active: false), Policy).Should().Be((2, (int?)null));
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public void BR_CART_01_a_merge_cap_shows_until_acknowledged()
    {
        var line = Line(10);
        line.QuantityLimitedFrom = 14;
        var product = Partner();

        CartRules.Evaluate(line, product, Policy).Notices.QuantityLimited.Should().Be(new QuantityLimitedNoticeDto(14, 10));

        CartRules.Acknowledge(line, product, Policy, Now);
        CartRules.Evaluate(line, product, Policy).Notices.QuantityLimited.Should().BeNull();
    }

    [Fact]
    [Trait("BR", "BR-CART-02")]
    public void BR_CART_02_acknowledging_moves_seen_to_current_and_keeps_what_still_blocks()
    {
        var line = Line(4, seenPrice: 49.90m, seen: AvailabilityStatus.InStock);
        var product = Partner(price: 54.90m, quantity: 2);

        CartRules.Acknowledge(line, product, Policy, Now);

        line.Should().BeEquivalentTo(new { UnitPriceSeenUsd = 54.90m, AvailabilitySeen = AvailabilityStatus.LowStock, SellableSeen = true, NoticeAcknowledgedUtc = Now });
        var state = CartRules.Evaluate(line, product, Policy);
        state.Notices.Should().Be(new CartLineNoticesDto(null, null, null, new QuantityLimitedNoticeDto(4, 2)));
        state.BlocksCheckout.Should().BeTrue();
    }
}
