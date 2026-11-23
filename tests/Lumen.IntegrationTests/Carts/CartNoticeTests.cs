using System.Net;
using Lumen.Api.Carts;
using Lumen.Api.Catalog;
using Lumen.Domain.Catalog;
using Lumen.IntegrationTests.Catalog;
using Lumen.IntegrationTests.Infrastructure;

namespace Lumen.IntegrationTests.Carts;

/// <summary>
/// BR-CART-02 through the cart API: a line remembers what the customer saw, says what changed since, until they
/// acknowledge it; totals always use current prices (with Standard shipping per seller and per-line tax).
/// </summary>
public sealed class CartNoticeTests(LumenApiFactory factory) : IAsyncLifetime
{
    private readonly List<Guid> _partnerProducts = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    // The partner sync tests delete every partner product afterwards; a cart line still pointing at one would block that.
    public async ValueTask DisposeAsync() => await factory.DeleteCartLinesAsync(_partnerProducts);

    private async Task<Product> PartnerAsync(string source, decimal price, int? quantity = 20)
    {
        var product = await factory.PartnerProductAsync(source, price, quantity);
        _partnerProducts.Add(product.Id);
        return product;
    }

    [Fact]
    [Trait("BR", "BR-CART-02")]
    public async Task BR_CART_02_a_price_change_shows_a_notice_until_acknowledged_and_totals_use_the_current_price()
    {
        var tote = await PartnerAsync(ProductSources.Harbor, 49.90m);
        var (guest, _) = factory.NewBrowser();
        var added = (await guest.AddOkAsync(tote.Id, 2)).LineFor(tote);
        added.Notices.Should().Be(CartLineNoticesDto.None);
        added.UnitPriceSeen.Should().Be(49.90m);

        // Harbor's price syncs to $54.90.
        await factory.UpdateProductAsync(tote.Id, p => p.PriceUsd = 54.90m);
        var cart = await guest.GetCartAsync();

        var line = cart.LineFor(tote);
        line.Notices.PriceChanged.Should().Be(new PriceChangedNoticeDto(49.90m, 54.90m));
        line.UnitPrice.Should().Be(54.90m);
        line.UnitPriceSeen.Should().Be(49.90m);
        line.LineTotal.Should().Be(109.80m);
        line.BlocksCheckout.Should().BeFalse("a price change is shown, not blocking; checkout re-confirms prices anyway");
        cart.Totals.Should().Be(new CartTotalsDto(ItemCount: 2, Subtotal: 109.80m, Shipping: 4.99m, Tax: 8.78m, Total: 123.57m));

        var acknowledged = await (await guest.AcknowledgeAsync(line.Id)).ReadAsync<CartDto>();
        acknowledged.LineFor(tote).Notices.Should().Be(CartLineNoticesDto.None);
        acknowledged.LineFor(tote).UnitPriceSeen.Should().Be(54.90m);
        acknowledged.Totals.Total.Should().Be(123.57m);
        (await guest.GetCartAsync()).LineFor(tote).Notices.PriceChanged.Should().BeNull("acknowledging is saved");
    }

    [Fact]
    [Trait("BR", "BR-CART-02")]
    public async Task BR_CART_02_a_price_drop_shows_a_notice_too()
    {
        var mug = await factory.OwnProductAsync(price: 18.50m);
        var (guest, _) = factory.NewBrowser();
        await guest.AddOkAsync(mug.Id, 1);

        await factory.UpdateProductAsync(mug.Id, p => p.PriceUsd = 15.00m);

        var line = (await guest.GetCartAsync()).LineFor(mug);
        line.Notices.PriceChanged.Should().Be(new PriceChangedNoticeDto(18.50m, 15.00m));
        line.LineTotal.Should().Be(15.00m);
    }

    [Fact]
    [Trait("BR", "BR-CART-02")]
    public async Task BR_CART_02_stock_dropping_below_the_line_says_only_2_left_and_acknowledging_keeps_the_block()
    {
        var socks = await PartnerAsync(ProductSources.Kestrel, 16.22m, quantity: 20);
        var (guest, _) = factory.NewBrowser();
        var line = (await guest.AddOkAsync(socks.Id, 4)).LineFor(socks);

        // Kestrel's stock file now says 2.
        await factory.UpdateProductAsync(socks.Id, p => p.Quantity = 2);
        var cart = await guest.GetCartAsync();

        cart.LineFor(socks).Notices.Should().Be(new CartLineNoticesDto(
            PriceChanged: null,
            AvailabilityChanged: new AvailabilityChangedNoticeDto(AvailabilityStatus.InStock, AvailabilityStatus.LowStock),
            Unavailable: null,
            QuantityLimited: new QuantityLimitedNoticeDto(4, 2)));
        cart.LineFor(socks).Product.Availability.Should().Be(new AvailabilityDto("low_stock", 2));
        cart.CanCheckout.Should().BeFalse();

        var acknowledged = (await (await guest.AcknowledgeAsync(line.Id)).ReadAsync<CartDto>()).LineFor(socks);
        acknowledged.Notices.AvailabilityChanged.Should().BeNull();
        acknowledged.Notices.QuantityLimited.Should().Be(new QuantityLimitedNoticeDto(4, 2), "it describes the stock now, until reduced");
        acknowledged.BlocksCheckout.Should().BeTrue();
    }

    [Fact]
    [Trait("BR", "BR-CART-02")]
    public async Task BR_CART_02_better_availability_shows_no_notice()
    {
        var product = await factory.OwnProductAsync(onHand: 3);
        var (guest, _) = factory.NewBrowser();
        await guest.AddOkAsync(product.Id, 1);

        await factory.UpdateProductAsync(product.Id, p => p.OnHand = 40);

        (await guest.GetCartAsync()).LineFor(product).Notices.Should().Be(CartLineNoticesDto.None);
    }

    [Fact]
    [Trait("BR", "BR-CART-02")]
    public async Task BR_CART_02_a_product_dropped_by_its_partner_says_no_longer_available()
    {
        var parka = await PartnerAsync(ProductSources.Kestrel, 1355.17m, quantity: 2);
        var (guest, _) = factory.NewBrowser();
        await guest.AddOkAsync(parka.Id, 1);

        await factory.UpdateProductAsync(parka.Id, p => p.IsListed = false);
        var cart = await guest.GetCartAsync();

        cart.LineFor(parka).Notices.Unavailable.Should().Be(CartLineUnavailableReason.NoLongerAvailable);
        cart.CanCheckout.Should().BeFalse();
        cart.Totals.Should().Be(CartTotalsDto.Zero);
    }

    [Fact]
    [Trait("BR", "BR-CART-02")]
    public async Task BR_CART_02_the_subtotal_item_count_leaves_out_unavailable_lines_but_the_badge_count_keeps_them()
    {
        var mug = await factory.OwnProductAsync(price: 18.50m);
        var tote = await factory.OwnProductAsync(price: 19.00m);
        var (guest, _) = factory.NewBrowser();
        await guest.AddOkAsync(mug.Id, 2);
        await guest.AddOkAsync(tote.Id, 10);

        // The admin deactivates the tote: its 10 units can no longer be bought.
        await factory.UpdateProductAsync(tote.Id, p => p.IsActive = false);
        var cart = await guest.GetCartAsync();

        cart.LineFor(tote).Notices.Unavailable.Should().Be(CartLineUnavailableReason.NoLongerAvailable);
        cart.ItemCount.Should().Be(12, "the mini-cart badge counts every unit in the cart (A-46)");
        cart.Totals.ItemCount.Should().Be(2, "\"Subtotal (n items)\" counts only the units the subtotal covers");
        cart.Totals.Subtotal.Should().Be(37.00m);
    }

    [Fact]
    [Trait("BR", "BR-CART-02")]
    public async Task BR_CART_02_acknowledging_the_cart_clears_every_lines_notices()
    {
        var first = await factory.OwnProductAsync(price: 10.00m);
        var second = await factory.OwnProductAsync(price: 20.00m);
        var (guest, _) = factory.NewBrowser();
        await guest.AddOkAsync(first.Id, 1);
        await guest.AddOkAsync(second.Id, 1);
        await factory.UpdateProductAsync(first.Id, p => p.PriceUsd = 11.00m);
        await factory.UpdateProductAsync(second.Id, p => p.PriceUsd = 19.00m);
        (await guest.GetCartAsync()).Lines.Should().OnlyContain(l => l.Notices.PriceChanged != null);

        var response = await guest.AcknowledgeAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.ReadAsync<CartDto>()).Lines.Should().OnlyContain(l => l.Notices == CartLineNoticesDto.None);
    }

    [Fact]
    [Trait("BR", "BR-CART-02")]
    public async Task BR_CART_02_totals_preview_ships_standard_per_seller_and_taxes_each_line()
    {
        var mug = await factory.OwnProductAsync(price: 18.50m);
        var tote = await PartnerAsync(ProductSources.Harbor, 49.90m);
        var socks = await PartnerAsync(ProductSources.Kestrel, 16.22m);
        var (guest, _) = factory.NewBrowser();

        await guest.AddOkAsync(mug.Id, 2);
        await guest.AddOkAsync(tote.Id, 1);
        var cart = await guest.AddOkAsync(socks.Id, 3);

        // Lines $37.00 + $49.90 + $48.66; tax per line $2.96 + $3.99 + $3.89; Standard shipping 3 × $4.99.
        cart.Totals.Should().Be(new CartTotalsDto(ItemCount: 6, Subtotal: 135.56m, Shipping: 14.97m, Tax: 10.84m, Total: 161.37m));
        cart.Totals.IsEstimate.Should().BeTrue();
        cart.ItemCount.Should().Be(6, "the mini-cart counts units, not lines (A-46)");
        cart.CanCheckout.Should().BeTrue();
        cart.LineFor(tote).Product.SellerName.Should().Be("Harbor Supply Co.");
        cart.LineFor(socks).Product.SellerName.Should().Be("Kestrel Outfitters");
        cart.LineFor(mug).Product.Should().BeEquivalentTo(new { Source = "lumen", SellerName = "Lumen Goods", Sellable = true });
        cart.LineFor(mug).Product.ImageUrl.Should().StartWith($"http://localhost/media/{mug.Id}/0?v=");
        cart.Lines.Select(l => l.Product.Id).Should().Equal([mug.Id, tote.Id, socks.Id], "lines are listed in the order they were added");
    }

    [Fact]
    [Trait("BR", "BR-CART-02")]
    public async Task BR_CART_02_money_is_written_with_two_decimals()
    {
        var product = await factory.OwnProductAsync(price: 20.00m);
        var (guest, _) = factory.NewBrowser();
        await guest.AddOkAsync(product.Id, 1);

        var json = await (await guest.GetAsync(CartTestSupport.CartUrl)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        json.Should().Contain("\"unitPrice\":20.00").And.Contain("\"subtotal\":20.00").And.Contain("\"total\":26.59")
            .And.Contain("\"isEstimate\":true");
    }
}
