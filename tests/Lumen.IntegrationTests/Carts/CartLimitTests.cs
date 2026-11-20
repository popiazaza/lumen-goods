using System.Net;
using System.Text;
using Lumen.Api.Carts;
using Lumen.IntegrationTests.Catalog;
using Lumen.IntegrationTests.Infrastructure;

namespace Lumen.IntegrationTests.Carts;

/// <summary>BR-CART-03 through the cart API: 1 to 10 per line, never above the known stock, nothing unbuyable added.</summary>
public sealed class CartLimitTests(LumenApiFactory factory)
{
    [Theory]
    [Trait("BR", "BR-CART-03")]
    [InlineData(11)]
    [InlineData(0)]
    public async Task BR_CART_03_a_quantity_outside_1_to_10_is_rejected_and_the_cart_is_unchanged(int quantity)
    {
        var product = await factory.OwnProductAsync();
        var (guest, _) = factory.NewBrowser();

        var response = await guest.AddAsync(product.Id, quantity);

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest);
        problem.GetProperty("errors").EnumerateObject()
            .Should().ContainSingle(e => e.Name.Equals("quantity", StringComparison.OrdinalIgnoreCase))
            .Which.Value[0].GetString().Should().Contain("maximum 10 per item");
        (await guest.GetCartAsync()).Should().BeEquivalentTo(CartDto.Empty);
    }

    [Theory]
    [InlineData("1", "-1")]
    [InlineData("11", "1")]
    public async Task A_body_that_names_quantity_twice_is_rejected_whichever_value_comes_last(string first, string second)
    {
        var product = await factory.OwnProductAsync();
        var (guest, _) = factory.NewBrowser();
        await guest.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{CartTestSupport.CartUrl}/lines")
        {
            Content = new StringContent(
                $$"""{"productId":"{{product.Id}}","quantity":{{first}},"quantity":{{second}}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-XSRF-TOKEN", guest.XsrfToken);

        var response = await guest.Client.SendAsync(request, CartTestSupport.Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest);
        (await guest.GetCartAsync()).Should().BeEquivalentTo(CartDto.Empty);
    }

    [Fact]
    [Trait("BR", "BR-CART-03")]
    public async Task BR_CART_03_adding_to_a_line_past_10_in_total_is_rejected_with_maximum_10_per_item()
    {
        var product = await factory.OwnProductAsync();
        var (guest, _) = factory.NewBrowser();
        await guest.AddOkAsync(product.Id, 8);

        var problem = await (await guest.AddAsync(product.Id, 3)).ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, CartProblems.QuantityLimitType);

        problem.GetProperty("title").GetString().Should().Be("Maximum 10 per item");
        problem.GetProperty("maxQuantity").GetInt32().Should().Be(10);
        problem.GetProperty("inCart").GetInt32().Should().Be(8);
        (await guest.GetCartAsync()).LineFor(product).Quantity.Should().Be(8);

        (await guest.AddOkAsync(product.Id, 2)).LineFor(product).Quantity.Should().Be(10);
    }

    [Fact]
    [Trait("BR", "BR-CART-03")]
    public async Task BR_CART_03_a_line_never_exceeds_the_known_available_quantity()
    {
        var product = await factory.OwnProductAsync(onHand: 3);
        var (guest, _) = factory.NewBrowser();

        var problem = await (await guest.AddAsync(product.Id, 4)).ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, CartProblems.QuantityLimitType);
        problem.GetProperty("title").GetString().Should().Be("Only 3 available");
        problem.GetProperty("maxQuantity").GetInt32().Should().Be(3);

        var cart = await guest.AddOkAsync(product.Id, 3);
        var line = cart.LineFor(product);
        line.MaxQuantity.Should().Be(3);
        line.BlocksCheckout.Should().BeFalse();

        (await (await guest.SetQuantityAsync(line.Id, 4)).ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, CartProblems.QuantityLimitType))
            .GetProperty("title").GetString().Should().Be("Only 3 available");
        var reduced = await (await guest.SetQuantityAsync(line.Id, 2)).ReadAsync<CartDto>();
        reduced.LineFor(product).Quantity.Should().Be(2);
        reduced.ItemCount.Should().Be(2);
    }

    [Fact]
    [Trait("BR", "BR-CART-03")]
    public async Task BR_CART_03_the_refusal_names_the_limit_that_applies_the_stock_when_it_is_below_10()
    {
        var product = await factory.OwnProductAsync(onHand: 3);
        var (guest, _) = factory.NewBrowser();
        await guest.AddOkAsync(product.Id, 2);

        // 2 + 9 is above 10 too, but the limit that applies is the stock of 3.
        var problem = await (await guest.AddAsync(product.Id, 9)).ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, CartProblems.QuantityLimitType);

        problem.GetProperty("title").GetString().Should().Be("Only 3 available");
        problem.GetProperty("maxQuantity").GetInt32().Should().Be(3);
        problem.GetProperty("inCart").GetInt32().Should().Be(2);
    }

    [Fact]
    [Trait("BR", "BR-CART-03")]
    public async Task BR_CART_03_a_product_that_is_not_sellable_cannot_be_added_and_the_cart_is_unchanged()
    {
        var deactivated = await factory.OwnProductAsync();
        await factory.UpdateProductAsync(deactivated.Id, p => p.IsActive = false);
        var priceOnRequest = await factory.OwnProductAsync();
        await factory.UpdateProductAsync(priceOnRequest.Id, p => p.PriceUsd = null);
        var (guest, jar) = factory.NewBrowser();

        foreach (var product in new[] { deactivated, priceOnRequest })
        {
            var problem = await (await guest.AddAsync(product.Id, 1)).ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, CartProblems.ProductUnavailableType);
            problem.GetProperty("reason").GetString().Should().Be("no_longer_available");
            problem.GetProperty("title").GetString().Should().Be("No longer available");
        }

        (await guest.GetCartAsync()).Should().BeEquivalentTo(CartDto.Empty);
        jar.GuestToken().Should().BeNull("a refused add creates no cart and no cookie");
    }

    [Fact]
    [Trait("BR", "BR-CART-03")]
    public async Task BR_CART_03_an_out_of_stock_or_unknown_product_cannot_be_added()
    {
        var soldOut = await factory.OwnProductAsync(onHand: 0);
        var (guest, _) = factory.NewBrowser();

        (await (await guest.AddAsync(soldOut.Id, 1)).ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, CartProblems.ProductUnavailableType))
            .GetProperty("reason").GetString().Should().Be("out_of_stock");
        (await (await guest.AddAsync(Guid.NewGuid(), 1)).ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, CartProblems.ProductUnavailableType))
            .GetProperty("reason").GetString().Should().Be("not_found");
        (await guest.GetCartAsync()).Lines.Should().BeEmpty();
    }

    [Fact]
    [Trait("BR", "BR-CART-03")]
    public async Task BR_CART_03_a_deactivated_product_stays_in_the_cart_as_no_longer_available_and_blocks_checkout_until_removed()
    {
        var mug = await factory.OwnProductAsync(price: 18.50m);
        var tote = await factory.OwnProductAsync(price: 30.00m);
        var (guest, _) = factory.NewBrowser();
        await guest.AddOkAsync(mug.Id, 2);
        (await guest.AddOkAsync(tote.Id, 1)).CanCheckout.Should().BeTrue();

        await factory.UpdateProductAsync(tote.Id, p => p.IsActive = false);
        var cart = await guest.GetCartAsync();

        var line = cart.LineFor(tote);
        line.Notices.Unavailable.Should().Be(CartLineUnavailableReason.NoLongerAvailable);
        line.BlocksCheckout.Should().BeTrue();
        line.MaxQuantity.Should().Be(0);
        line.Product.Sellable.Should().BeFalse();
        cart.CanCheckout.Should().BeFalse();
        cart.ItemCount.Should().Be(3);
        cart.Totals.Subtotal.Should().Be(37.00m, "a line that cannot be bought is left out of the totals");

        (await (await guest.AcknowledgeAsync(line.Id)).ReadAsync<CartDto>()).LineFor(tote).BlocksCheckout
            .Should().BeTrue("acknowledging does not make a product buyable");
        await (await guest.SetQuantityAsync(line.Id, 2)).ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, CartProblems.ProductUnavailableType);

        var afterRemove = await (await guest.RemoveAsync(line.Id)).ReadAsync<CartDto>();
        afterRemove.Lines.Should().ContainSingle().Which.Product.Id.Should().Be(mug.Id);
        afterRemove.CanCheckout.Should().BeTrue();
    }

    [Fact]
    [Trait("BR", "BR-CART-03")]
    public async Task BR_CART_03_a_line_above_the_stock_left_blocks_checkout_until_reduced()
    {
        var product = await factory.OwnProductAsync(onHand: 20);
        var (guest, _) = factory.NewBrowser();
        var line = (await guest.AddOkAsync(product.Id, 4)).LineFor(product);

        // Another customer's order reserves stock: 2 left.
        await factory.UpdateProductAsync(product.Id, p => p.Reserved = 18);
        var cart = await guest.GetCartAsync();

        cart.LineFor(product).Should().BeEquivalentTo(new
        {
            Quantity = 4,
            MaxQuantity = 2,
            BlocksCheckout = true,
            Notices = new { QuantityLimited = new QuantityLimitedNoticeDto(4, 2), Unavailable = (CartLineUnavailableReason?)null },
        });
        cart.CanCheckout.Should().BeFalse();

        var reduced = await (await guest.SetQuantityAsync(line.Id, 2)).ReadAsync<CartDto>();
        reduced.LineFor(product).Notices.QuantityLimited.Should().BeNull();
        reduced.CanCheckout.Should().BeTrue();
    }

    [Fact]
    [Trait("BR", "BR-CART-03")]
    public async Task BR_CART_03_lines_of_another_cart_are_never_reachable()
    {
        var product = await factory.OwnProductAsync();
        var (owner, _) = factory.NewBrowser();
        var line = (await owner.AddOkAsync(product.Id, 2)).LineFor(product);

        var (stranger, _) = factory.NewBrowser();
        await stranger.StartAsync();
        var customer = factory.CreateSession();
        await customer.RegisterAsync();

        foreach (var other in new[] { stranger, customer })
        {
            (await other.GetCartAsync()).Lines.Should().BeEmpty();
            await (await other.SetQuantityAsync(line.Id, 5)).ShouldBeProblemAsync(HttpStatusCode.NotFound, CartProblems.LineNotFoundType);
            await (await other.RemoveAsync(line.Id)).ShouldBeProblemAsync(HttpStatusCode.NotFound, CartProblems.LineNotFoundType);
            await (await other.AcknowledgeAsync(line.Id)).ShouldBeProblemAsync(HttpStatusCode.NotFound, CartProblems.LineNotFoundType);
        }

        (await owner.GetCartAsync()).LineFor(product).Quantity.Should().Be(2);
    }
}
