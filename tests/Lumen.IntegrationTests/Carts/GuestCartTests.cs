using System.Globalization;
using System.Net;
using Lumen.Api.Carts;
using Lumen.Api.Partners;
using Lumen.IntegrationTests.Catalog;
using Lumen.IntegrationTests.Infrastructure;
using Lumen.IntegrationTests.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Lumen.IntegrationTests.Carts;

/// <summary>
/// BR-CART-01: who a cart belongs to. A guest's cart is found only by the random token in an HttpOnly cookie (the
/// database keeps its hash), lives 30 days from its last use, and a customer's saved cart only by the session.
/// </summary>
public sealed class GuestCartTests(LumenApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_the_first_add_issues_an_http_only_strict_30_day_cookie_and_stores_only_its_hash()
    {
        var product = await factory.OwnProductAsync();
        var (guest, jar) = factory.NewBrowser();

        var response = await guest.AddAsync(product.Id, 1);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith($"{GuestCartToken.CookieName}=", StringComparison.Ordinal));
        var attributes = setCookie.Split(';', StringSplitOptions.TrimEntries).Skip(1).ToList();
        attributes.Should().Contain(["httponly", "samesite=strict", "path=/api"]);
        var expires = DateTimeOffset.Parse(attributes.Single(a => a.StartsWith("expires=", StringComparison.Ordinal))["expires=".Length..], CultureInfo.InvariantCulture);
        expires.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(30), TimeSpan.FromMinutes(2));

        var token = jar.GuestToken()!;
        token.Should().HaveLength(GuestCartToken.TokenLength);
        (await factory.GuestCartExistsAsync(token)).Should().BeTrue();
        var stored = await factory.WithDbResultAsync(db => db.Carts.AsNoTracking()
            .Where(c => c.Lines.Any(l => l.ProductId == product.Id)).Select(c => c.AnonymousTokenHash).SingleAsync(Ct));
        stored.Should().Equal(GuestCartToken.Hash(token)).And.NotEqual(System.Text.Encoding.UTF8.GetBytes(token));
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_reading_an_empty_cart_creates_nothing()
    {
        var (guest, jar) = factory.NewBrowser();

        var response = await guest.GetAsync(CartTestSupport.CartUrl);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.ReadAsync<CartDto>()).Should().BeEquivalentTo(CartDto.Empty);
        jar.GuestToken().Should().BeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue("a cart is personal and must never be cached");
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_a_cart_change_without_the_xsrf_header_is_refused()
    {
        var product = await factory.OwnProductAsync();
        var (guest, _) = factory.NewBrowser();
        await guest.StartAsync();

        var response = await guest.PostAsJsonAsync($"{CartTestSupport.CartUrl}/lines", new { productId = product.Id, quantity = 1 }, withXsrfHeader: false);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await guest.GetCartAsync()).Lines.Should().BeEmpty();
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_a_guest_cart_is_reached_only_with_its_own_cookie()
    {
        var product = await factory.OwnProductAsync();
        var (owner, ownerJar) = factory.NewBrowser();
        await owner.AddOkAsync(product.Id, 2);

        var (forger, forgerJar) = factory.NewBrowser();
        forgerJar.SetGuestToken(GuestCartToken.NewToken());
        (await forger.GetCartAsync()).Lines.Should().BeEmpty();

        var (sameCookie, sameJar) = factory.NewBrowser();
        sameJar.SetGuestToken(ownerJar.GuestToken()!);
        (await sameCookie.GetCartAsync()).LineFor(product).Quantity.Should().Be(2, "the cookie is the guest's only key to the cart");
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_a_customers_saved_cart_is_reached_only_by_signing_in_as_them()
    {
        var product = await factory.OwnProductAsync();
        var customer = factory.CreateSession();
        await customer.RegisterAsync();
        var line = (await customer.AddOkAsync(product.Id, 3)).LineFor(product);

        var otherCustomer = factory.CreateSession();
        await otherCustomer.RegisterAsync();
        var (guest, _) = factory.NewBrowser();
        await guest.StartAsync();

        foreach (var other in new[] { otherCustomer, guest })
        {
            (await other.GetCartAsync()).Lines.Should().BeEmpty();
            await (await other.SetQuantityAsync(line.Id, 1)).ShouldBeProblemAsync(HttpStatusCode.NotFound, CartProblems.LineNotFoundType);
            await (await other.RemoveAsync(line.Id)).ShouldBeProblemAsync(HttpStatusCode.NotFound, CartProblems.LineNotFoundType);
        }

        (await customer.GetCartAsync()).LineFor(product).Quantity.Should().Be(3);
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_staff_accounts_have_no_cart_and_get_403()
    {
        var product = await factory.OwnProductAsync();
        var (admin, _) = factory.NewBrowser();
        await admin.SignInAsync(CartTestSupport.AdminEmail, CartTestSupport.AdminPassword);

        await (await admin.GetAsync(CartTestSupport.CartUrl)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, CartProblems.CustomersOnlyType);
        await (await admin.AddAsync(product.Id, 1)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, CartProblems.CustomersOnlyType);
        await (await admin.AcknowledgeAsync()).ShouldBeProblemAsync(HttpStatusCode.Forbidden, CartProblems.CustomersOnlyType);

        var adminId = await factory.CustomerIdAsync(CartTestSupport.AdminEmail);
        (await factory.WithDbResultAsync(db => db.Carts.AnyAsync(c => c.CustomerId == adminId, Ct))).Should().BeFalse();
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_a_guest_cart_unused_for_30_days_is_gone_and_a_used_one_slides()
    {
        var product = await factory.OwnProductAsync();
        var (stale, staleJar) = factory.NewBrowser();
        await stale.AddOkAsync(product.Id, 1);
        var staleToken = staleJar.GuestToken()!;
        var (active, activeJar) = factory.NewBrowser();
        await active.AddOkAsync(product.Id, 1);
        var activeToken = activeJar.GuestToken()!;
        await SetLastUsedAsync(staleToken, DateTimeOffset.UtcNow.AddDays(-31));
        await SetLastUsedAsync(activeToken, DateTimeOffset.UtcNow.AddDays(-2));

        (await stale.GetCartAsync()).Lines.Should().BeEmpty("the cookie would have expired: the server does not honour it either");
        staleJar.GuestToken().Should().BeNull("the dead cookie is removed");

        (await active.GetCartAsync()).Lines.Should().ContainSingle();
        (await LastUsedAsync(activeToken)).Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), "reading the cart renews it");

        var restarted = await stale.AddOkAsync(product.Id, 1);
        restarted.ItemCount.Should().Be(1);
        staleJar.GuestToken().Should().NotBe(staleToken, "a new cart gets a new token");
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_the_clean_up_deletes_guest_carts_past_their_lifetime_and_never_saved_carts()
    {
        // A fixed clock in the past, so only carts older than it count as expired; other tests' carts are newer.
        var clock = new FakeTimeProvider(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var product = await factory.OwnProductAsync();
        var customerId = await factory.NewCustomerAsync();
        Cart Old(Guid? owner, int daysBeforeClock) => new()
        {
            CustomerId = owner,
            AnonymousTokenHash = owner is null ? GuestCartToken.Hash(GuestCartToken.NewToken()) : null,
            CreatedUtc = clock.GetUtcNow().AddDays(-daysBeforeClock),
            UpdatedUtc = clock.GetUtcNow().AddDays(-daysBeforeClock),
            Lines = [new CartLine
            {
                ProductId = product.Id, Quantity = 1, UnitPriceSeenUsd = 18.50m, AvailabilitySeen = Lumen.Domain.Catalog.AvailabilityStatus.InStock,
                SellableSeen = true, AddedUtc = clock.GetUtcNow().AddDays(-daysBeforeClock),
            }],
        };
        var expiredGuest = Old(null, 31);
        var recentGuest = Old(null, 29);
        var oldSaved = Old(customerId, 400);
        await factory.SaveAsync(expiredGuest, recentGuest, oldSaved);

        var job = new GuestCartCleanupJob(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<IOptions<CartOptions>>(),
            factory.Services.GetRequiredService<IOptionsMonitor<JobsOptions>>(),
            clock,
            NullLogger<GuestCartCleanupJob>.Instance);
        (await job.RunOnceAsync(Ct)).Should().BeGreaterThanOrEqualTo(1);

        var remaining = await factory.WithDbResultAsync(db => db.Carts.AsNoTracking()
            .Where(c => c.Id == expiredGuest.Id || c.Id == recentGuest.Id || c.Id == oldSaved.Id).Select(c => c.Id).ToListAsync(Ct));
        remaining.Should().BeEquivalentTo([recentGuest.Id, oldSaved.Id]);
        (await factory.WithDbResultAsync(db => db.CartLines.CountAsync(l => l.CartId == expiredGuest.Id, Ct))).Should().Be(0);
    }

    private Task SetLastUsedAsync(string token, DateTimeOffset whenUtc)
    {
        var hash = GuestCartToken.Hash(token);
        return factory.WithDbResultAsync(db => db.Carts.Where(c => c.AnonymousTokenHash == hash)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedUtc, whenUtc), Ct));
    }

    private Task<DateTimeOffset> LastUsedAsync(string token)
    {
        var hash = GuestCartToken.Hash(token);
        return factory.WithDbResultAsync(db => db.Carts.Where(c => c.AnonymousTokenHash == hash).Select(c => c.UpdatedUtc).SingleAsync(Ct));
    }
}
