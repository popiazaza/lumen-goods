using Lumen.Api.Carts;
using Lumen.Api.Catalog;
using Lumen.Domain.Catalog;
using Lumen.IntegrationTests.Catalog;
using Lumen.IntegrationTests.Infrastructure;
using Lumen.IntegrationTests.Orders;
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
        var customerId = await factory.NewShopperAsync();
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
        var customerId = await factory.NewShopperAsync();
        var (first, firstToken) = GuestCart((a, 1));
        var (second, secondToken) = GuestCart((b, 2));
        await factory.SaveAsync(first, second);

        await Task.WhenAll(MergeAsync(firstToken, customerId), MergeAsync(secondToken, customerId));

        var saved = await LoadCustomerCartAsync(customerId);
        saved.Lines.Select(l => (l.ProductId, l.Quantity)).Should().BeEquivalentTo([(a.Id, 1), (b.Id, 2)]);
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_a_merge_failing_at_commit_keeps_the_guest_cart_and_cookie_and_a_later_sign_in_merges_once()
    {
        var a = await factory.OwnProductAsync();
        var b = await factory.OwnProductAsync();
        var customerId = await factory.NewShopperAsync();
        await factory.SaveAsync(SavedCart(customerId, (a, 1)));
        var (guest, token) = GuestCart((a, 2), (b, 1));
        await factory.SaveAsync(guest);

        // Every statement of the merge runs (lines summed and moved, guest cart deleted), then the commit fails.
        var failingCommit = new FailingCommit();
        await using (var database = factory.DatabaseWith(failingCommit))
        {
            var http = CartFaults.GuestRequest(token, factory.Services);
            await FluentActions.Awaiting(() => factory.MergeServiceOn(database).MergeGuestCartAsync(http, customerId, Ct))
                .Should().ThrowAsync<InvalidOperationException>().WithMessage(FailingCommit.Message);

            failingCommit.Commits.Should().Be(1);
            http.GuestCookieHeaders().Should().BeEmpty("the cookie is only expired after the merge committed");
        }

        (await factory.GuestCartExistsAsync(token)).Should().BeTrue("the failed merge was rolled back");
        (await LoadCustomerCartAsync(customerId)).Lines.Select(l => (l.ProductId, l.Quantity)).Should().BeEquivalentTo([(a.Id, 1)]);

        // The next sign-in merges it, once.
        await MergeAsync(token, customerId);
        await MergeAsync(token, customerId);

        (await LoadCustomerCartAsync(customerId)).Lines.Select(l => (l.ProductId, l.Quantity)).Should().BeEquivalentTo([(a.Id, 3), (b.Id, 1)]);
        (await factory.GuestCartExistsAsync(token)).Should().BeFalse();
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_a_merge_that_conflicts_5_times_gives_up_and_keeps_the_guest_cart_for_the_next_sign_in()
    {
        var a = await factory.OwnProductAsync();
        var customerId = await factory.NewShopperAsync();
        await factory.SaveAsync(SavedCart(customerId, (a, 1)));
        var (guest, token) = GuestCart((a, 2));
        await factory.SaveAsync(guest);
        var hash = GuestCartToken.Hash(token);

        // Another tab changes the guest cart before every save, so every attempt's delete misses its xmin.
        var interference = new BeforeSave(_ => factory.WithDbAsync(db => db.Carts.Where(c => c.AnonymousTokenHash == hash)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.UpdatedUtc, DateTimeOffset.UtcNow), CancellationToken.None)));
        await using (var database = factory.DatabaseWith(interference))
        {
            var http = CartFaults.GuestRequest(token, factory.Services);
            await factory.MergeServiceOn(database).MergeGuestCartAsync(http, customerId, Ct);

            interference.Saves.Should().Be(CartQueries.MaxAttempts);
            http.GuestCookieHeaders().Should().BeEmpty();
        }

        (await factory.GuestCartExistsAsync(token)).Should().BeTrue();
        (await LoadCustomerCartAsync(customerId)).Lines.Should().ContainSingle().Which.Quantity.Should().Be(1);

        await MergeAsync(token, customerId);

        (await LoadCustomerCartAsync(customerId)).Lines.Should().ContainSingle().Which.Quantity.Should().Be(3);
    }

    [Theory]
    [Trait("BR", "BR-CART-01")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BR_CART_01_a_guest_change_racing_the_sign_in_that_merges_its_cart_is_a_409_not_a_hidden_new_cart(bool newProduct)
    {
        var a = await factory.OwnProductAsync();
        var b = await factory.OwnProductAsync();
        var customerId = await factory.NewShopperAsync();
        var (guest, token) = GuestCart((a, 1));
        await factory.SaveAsync(guest);

        // The add reads the guest cart, then sign-in in another tab merges it away before the add saves.
        var signIn = new BeforeSave(save => save == 1 ? MergeAsync(token, customerId) : Task.CompletedTask);
        await using var database = factory.DatabaseWith(signIn);
        await using var scope = database.CreateAsyncScope();
        var http = CartFaults.GuestRequest(token, factory.Services);

        var edit = await factory.CartServiceOn(scope.ServiceProvider.GetRequiredService<Lumen.Api.Infrastructure.Data.LumenDbContext>())
            .AddAsync(http, (newProduct ? b : a).Id, 1, Ct);

        edit.Problem.Should().NotBeNull();
        edit.Problem!.Status.Should().Be(409);
        edit.Problem.Type.Should().Be(CartProblems.CartChangedType);
        signIn.Saves.Should().Be(1, "the retry finds the guest cart gone and saves nothing");
        http.GuestCookieHeaders().Should().ContainSingle().Which.Should().Contain("expires=Thu, 01 Jan 1970");
        (await LoadCustomerCartAsync(customerId)).Lines.Select(l => (l.ProductId, l.Quantity)).Should().BeEquivalentTo([(a.Id, 1)]);
        (await factory.WithDbResultAsync(db => db.Carts.CountAsync(
            c => c.CustomerId == null && c.Lines.Any(l => l.ProductId == a.Id || l.ProductId == b.Id), Ct))).Should().Be(0, "no hidden guest cart");
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_a_request_still_carrying_a_merged_guest_cookie_is_told_to_reload()
    {
        var a = await factory.OwnProductAsync();
        var b = await factory.OwnProductAsync();
        var (browser, jar) = factory.NewBrowser();
        await browser.AddOkAsync(a.Id, 1);
        var token = jar.GuestToken()!;
        var customerId = await factory.NewShopperAsync();
        await MergeAsync(token, customerId);

        var response = await browser.AddAsync(b.Id, 1);

        await response.ShouldBeProblemAsync(System.Net.HttpStatusCode.Conflict, CartProblems.CartChangedType);
        jar.GuestToken().Should().BeNull("the dead cookie is dropped, so a guest's retry starts a new cart");
        (await LoadCustomerCartAsync(customerId)).Lines.Should().ContainSingle(l => l.ProductId == a.Id);
        (await browser.AddOkAsync(b.Id, 1)).Lines.Should().ContainSingle(l => l.Product.Id == b.Id);
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_signing_in_as_staff_merges_nothing_and_leaves_the_guest_cart_with_the_browser()
    {
        var a = await factory.OwnProductAsync();
        var (browser, jar) = factory.NewBrowser();
        await browser.AddOkAsync(a.Id, 1);
        var token = jar.GuestToken()!;

        await browser.SignInAsync(CartTestSupport.AdminEmail, CartTestSupport.AdminPassword);

        await (await browser.GetAsync(CartTestSupport.CartUrl)).ShouldBeProblemAsync(System.Net.HttpStatusCode.Forbidden, CartProblems.CustomersOnlyType);
        (await factory.GuestCartExistsAsync(token)).Should().BeTrue();
        var adminId = await factory.CustomerIdAsync(CartTestSupport.AdminEmail);
        (await factory.WithDbResultAsync(db => db.Carts.AnyAsync(c => c.CustomerId == adminId, Ct))).Should().BeFalse();

        await browser.SignOutAsync();
        (await browser.GetCartAsync()).LineFor(a).Quantity.Should().Be(1, "the guest cart is the browser's again");
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

    /// <summary>What sign-in does with the guest cookie it receives.</summary>
    private async Task MergeAsync(string guestToken, Guid customerId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var http = CartFaults.GuestRequest(guestToken, scope.ServiceProvider);
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
