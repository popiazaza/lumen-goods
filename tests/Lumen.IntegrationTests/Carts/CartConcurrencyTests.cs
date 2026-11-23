using System.Net;
using Lumen.IntegrationTests.Infrastructure;

namespace Lumen.IntegrationTests.Carts;

/// <summary>
/// One customer's cart edited from two tabs at the same moment loses no update: every change re-reads the cart and saves
/// it with the cart's xmin as the concurrency check, and a conflict (xmin moved, or both inserted the same cart or line)
/// is re-applied on top of the other change (<c>CartService.MaxAttempts</c>). Two signed-in sessions of one customer
/// stand in for two tabs; whichever way the requests interleave, the outcome must be the same.
/// </summary>
public sealed class CartConcurrencyTests(LumenApiFactory factory)
{
    [Fact]
    public async Task Two_tabs_adding_different_products_at_the_same_moment_keep_every_line()
    {
        var (first, second) = await TwoTabsOfOneCustomerAsync();
        var products = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => factory.OwnProductAsync()));

        // Five rounds of two adds at once give the requests many chances to collide.
        var answers = new List<HttpStatusCode>();
        for (var round = 0; round < 5; round++)
        {
            var pair = await Task.WhenAll(
                first.AddAsync(products[2 * round].Id, 1),
                second.AddAsync(products[(2 * round) + 1].Id, 1));
            answers.AddRange(pair.Select(r => r.StatusCode));
        }

        answers.Should().AllBeEquivalentTo(HttpStatusCode.OK);
        var cart = await first.GetCartAsync();
        cart.Lines.Select(l => l.Product.Id).Should().BeEquivalentTo(products.Select(p => p.Id));
        cart.Lines.Should().AllSatisfy(l => l.Quantity.Should().Be(1));
    }

    [Fact]
    public async Task Two_tabs_adding_the_same_product_before_the_cart_exists_end_with_one_line_of_both_units()
    {
        var (first, second) = await TwoTabsOfOneCustomerAsync();
        var product = await factory.OwnProductAsync();

        // Neither tab has a saved cart yet, so both try to create it and its one line for this product.
        var answers = await Task.WhenAll(first.AddAsync(product.Id, 1), second.AddAsync(product.Id, 1));

        answers.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.OK);
        var cart = await second.GetCartAsync();
        cart.Lines.Should().ContainSingle().Which.Quantity.Should().Be(2);
    }

    private async Task<(ApiSession First, ApiSession Second)> TwoTabsOfOneCustomerAsync()
    {
        var (first, _) = factory.NewBrowser();
        var email = await first.RegisterAsync();
        var (second, _) = factory.NewBrowser();
        await second.SignInAsync(email);
        return (first, second);
    }
}
