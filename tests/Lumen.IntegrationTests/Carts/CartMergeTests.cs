using Lumen.Api.Carts;
using Lumen.Api.Catalog;
using Lumen.Domain.Catalog;
using Lumen.IntegrationTests.Catalog;
using Lumen.IntegrationTests.Infrastructure;
using Lumen.IntegrationTests.Orders;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lumen.IntegrationTests.Carts;

/// <summary>BR-CART-01: a guest cart joins the customer's saved cart on sign-in and on registration, exactly once.</summary>
public sealed class CartMergeTests(LumenApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A registered customer whose saved cart holds <paramref name="lines"/>, signed out again.</summary>
    private async Task<string> CustomerWithSavedCartAsync(params (Product Product, int Quantity)[] lines)
    {
        var session = factory.CreateSession();
        var email = await session.RegisterAsync();
        foreach (var (product, quantity) in lines)
        {
            await session.AddOkAsync(product.Id, quantity);
        }

        await session.SignOutAsync();
        return email;
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_signing_in_sums_the_guest_cart_into_the_saved_cart_and_deletes_the_guest_cart()
    {
        var a = await factory.OwnProductAsync();
        var b = await factory.OwnProductAsync();
        var email = await CustomerWithSavedCartAsync((a, 1), (b, 1));
        var (browser, jar) = factory.NewBrowser();
        await browser.AddOkAsync(a.Id, 2);
        var guestToken = jar.GuestToken()!;

        await browser.SignInAsync(email);

        var cart = await browser.GetCartAsync();
        cart.Lines.Select(l => (l.Product.Id, l.Quantity)).Should().BeEquivalentTo([(a.Id, 3), (b.Id, 1)]);
        cart.LineFor(a).Notices.QuantityLimited.Should().BeNull();
        cart.ItemCount.Should().Be(4);
        (await factory.GuestCartExistsAsync(guestToken)).Should().BeFalse("the guest cart is deleted after the merge");
        jar.GuestToken().Should().BeNull("the guest cart cookie is expired");

        // Signed out again, the browser is a guest with an empty cart; the saved cart is still there on the next sign-in.
        await browser.SignOutAsync();
        (await browser.GetCartAsync()).Lines.Should().BeEmpty();
        await browser.SignInAsync(email);
        (await browser.GetCartAsync()).ItemCount.Should().Be(4);
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_merged_quantities_are_capped_at_10_with_a_quantity_limited_notice()
    {
        var a = await factory.OwnProductAsync();
        var email = await CustomerWithSavedCartAsync((a, 5));
        var (browser, _) = factory.NewBrowser();
        await browser.AddOkAsync(a.Id, 9);

        await browser.SignInAsync(email);

        var cart = await browser.GetCartAsync();
        var line = cart.LineFor(a);
        line.Quantity.Should().Be(10);
        line.Notices.QuantityLimited.Should().Be(new QuantityLimitedNoticeDto(Requested: 14, Allowed: 10));
        line.BlocksCheckout.Should().BeFalse("the merged line itself is within the limits");
        cart.CanCheckout.Should().BeTrue();

        var acknowledged = await (await browser.AcknowledgeAsync(line.Id)).ReadAsync<CartDto>();
        acknowledged.LineFor(a).Notices.QuantityLimited.Should().BeNull();
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_merged_quantities_are_capped_at_the_known_available_quantity()
    {
        var a = await factory.OwnProductAsync(onHand: 6);
        var email = await CustomerWithSavedCartAsync((a, 3));
        var (browser, _) = factory.NewBrowser();
        await browser.AddOkAsync(a.Id, 4);

        await browser.SignInAsync(email);

        var line = (await browser.GetCartAsync()).LineFor(a);
        line.Quantity.Should().Be(6);
        line.Notices.QuantityLimited.Should().Be(new QuantityLimitedNoticeDto(Requested: 7, Allowed: 6));
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_registering_brings_the_guest_cart_into_the_new_account()
    {
        var a = await factory.OwnProductAsync(price: 12.00m);
        var (browser, jar) = factory.NewBrowser();
        await browser.AddOkAsync(a.Id, 2);
        var guestToken = jar.GuestToken()!;
        await factory.UpdateProductAsync(a.Id, p => p.PriceUsd = 13.00m);

        var email = await browser.RegisterAsync();

        var line = (await browser.GetCartAsync()).LineFor(a);
        line.Quantity.Should().Be(2);
        line.Notices.PriceChanged.Should().Be(new PriceChangedNoticeDto(12.00m, 13.00m), "a moved line keeps what the guest saw");
        (await factory.GuestCartExistsAsync(guestToken)).Should().BeFalse();
        var customerId = await factory.CustomerIdAsync(email);
        (await factory.WithDbResultAsync(db => db.Carts.CountAsync(c => c.CustomerId == customerId, Ct))).Should().Be(1);
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_replaying_a_sign_in_with_the_old_guest_cookie_changes_nothing()
    {
        var a = await factory.OwnProductAsync();
        var email = await CustomerWithSavedCartAsync((a, 1));
        var (browser, jar) = factory.NewBrowser();
        await browser.AddOkAsync(a.Id, 2);
        var guestToken = jar.GuestToken()!;
        await browser.SignInAsync(email);
        (await browser.GetCartAsync()).LineFor(a).Quantity.Should().Be(3);

        // Someone replays the sign-in carrying the guest cookie from before it.
        var (replay, replayJar) = factory.NewBrowser();
        replayJar.SetGuestToken(guestToken);
        await replay.SignInAsync(email);

        (await replay.GetCartAsync()).LineFor(a).Quantity.Should().Be(3);
        (await browser.GetCartAsync()).LineFor(a).Quantity.Should().Be(3);
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_concurrent_merges_of_one_guest_cart_add_it_exactly_once()
    {
        var a = await factory.OwnProductAsync();
        var b = await factory.OwnProductAsync();
        var customerId = await factory.NewCustomerAsync();
        await factory.SaveAsync(SavedCart(customerId, (a, 1)));
        var (guest, token) = GuestCart((a, 2), (b, 1));
        await factory.SaveAsync(guest);

        // Four sign-ins carrying the same guest cookie, all at once.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => MergeAsync(token, customerId)));

        var saved = await LoadCustomerCartAsync(customerId);
        saved.Lines.Select(l => (l.ProductId, l.Quantity)).Should().BeEquivalentTo([(a.Id, 3), (b.Id, 1)]);
        (await factory.GuestCartExistsAsync(token)).Should().BeFalse();
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_two_guest_carts_merging_at_once_into_a_customer_without_a_cart_both_arrive()
    {
        var a = await factory.OwnProductAsync();
        var b = await factory.OwnProductAsync();
        var customerId = await factory.NewCustomerAsync();
        var (first, firstToken) = GuestCart((a, 1));
        var (second, secondToken) = GuestCart((b, 2));
        await factory.SaveAsync(first, second);

        await Task.WhenAll(MergeAsync(firstToken, customerId), MergeAsync(secondToken, customerId));

        var saved = await LoadCustomerCartAsync(customerId);
        saved.Lines.Select(l => (l.ProductId, l.Quantity)).Should().BeEquivalentTo([(a.Id, 1), (b.Id, 2)]);
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_signing_in_without_a_guest_cart_leaves_the_saved_cart_alone()
    {
        var a = await factory.OwnProductAsync();
        var email = await CustomerWithSavedCartAsync((a, 2));
        var (browser, jar) = factory.NewBrowser();
        jar.SetGuestToken(GuestCartToken.NewToken());

        await browser.SignInAsync(email);

        (await browser.GetCartAsync()).LineFor(a).Quantity.Should().Be(2);
    }

    private async Task MergeAsync(string guestToken, Guid customerId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Headers.Cookie = $"{GuestCartToken.CookieName}={guestToken}";
        await scope.ServiceProvider.GetRequiredService<ICartMergeService>().MergeGuestCartAsync(http, customerId, Ct);
    }

    private Task<Cart> LoadCustomerCartAsync(Guid customerId) =>
        factory.WithDbResultAsync(db => db.Carts.AsNoTracking().Include(c => c.Lines).SingleAsync(c => c.CustomerId == customerId, Ct));

    private static Cart SavedCart(Guid customerId, params (Product Product, int Quantity)[] lines) =>
        WithLines(new Cart { CustomerId = customerId, CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow }, lines);

    private static (Cart Cart, string Token) GuestCart(params (Product Product, int Quantity)[] lines)
    {
        var token = GuestCartToken.NewToken();
        var cart = new Cart { AnonymousTokenHash = GuestCartToken.Hash(token), CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow };
        return (WithLines(cart, lines), token);
    }

    private static Cart WithLines(Cart cart, (Product Product, int Quantity)[] lines)
    {
        foreach (var (product, quantity) in lines)
        {
            cart.Lines.Add(new CartLine
            {
                ProductId = product.Id,
                Quantity = quantity,
                UnitPriceSeenUsd = product.PriceUsd!.Value,
                AvailabilitySeen = AvailabilityStatus.InStock,
                SellableSeen = true,
                AddedUtc = DateTimeOffset.UtcNow,
            });
        }

        return cart;
    }
}
