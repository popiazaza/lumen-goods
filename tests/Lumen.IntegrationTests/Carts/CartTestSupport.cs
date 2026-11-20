using System.Net;
using System.Text.Json;
using Lumen.Api.Carts;
using Lumen.Api.Catalog;
using Lumen.Api.Identity;
using Lumen.Api.Infrastructure.Data;
using Lumen.Domain.Catalog;
using Lumen.IntegrationTests.Catalog;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lumen.IntegrationTests.Carts;

/// <summary>
/// Cart API helpers. Every test uses its own products and its own freshly registered customers, so tests never share
/// a cart. Partner products in carts must be cleaned up (<see cref="DeleteCartLinesAsync"/>): the partner sync tests
/// delete every partner product afterwards, which a cart line referencing one would block.
/// </summary>
internal static class CartTestSupport
{
    public const string CartUrl = "/api/v1/cart";

    public const string Password = "Shopper!2026";

    private static readonly Uri Origin = new("http://localhost");

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A guest browser whose cookie jar the test can read and write (to capture or replay the guest cart cookie).</summary>
    public static (ApiSession Session, CookieContainer Jar) NewBrowser(this LumenApiFactory factory)
    {
        var jar = new CookieContainer();
        return (new ApiSession(factory.CreateDefaultClient(Origin, new CookieContainerHandler(jar))), jar);
    }

    public static string? GuestToken(this CookieContainer jar) =>
        jar.GetCookies(new Uri(Origin, CartUrl))[GuestCartToken.CookieName]?.Value;

    public static void SetGuestToken(this CookieContainer jar, string token) =>
        jar.Add(Origin, new Cookie(GuestCartToken.CookieName, token, GuestCartCookie.Path));

    /// <summary>Registers a new customer in <paramref name="session"/> (which is then signed in) and returns the email.</summary>
    public static async Task<string> RegisterAsync(this ApiSession session)
    {
        var email = $"cart-{Guid.NewGuid():N}@example.test";
        if (session.XsrfToken is null)
        {
            await session.StartAsync();
        }

        (await session.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, Password))).StatusCode.Should().Be(HttpStatusCode.OK);
        return email;
    }

    public static async Task SignInAsync(this ApiSession session, string email)
    {
        if (session.XsrfToken is null)
        {
            await session.StartAsync();
        }

        (await session.PostAsJsonAsync("/api/v1/auth/sign-in", new SignInRequest(email, Password))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public static async Task SignOutAsync(this ApiSession session) =>
        (await session.PostAsJsonAsync("/api/v1/auth/sign-out", new { })).StatusCode.Should().Be(HttpStatusCode.OK);

    public static async Task<CartDto> GetCartAsync(this ApiSession session)
    {
        var response = await session.GetAsync(CartUrl);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.ReadAsync<CartDto>();
    }

    public static async Task<HttpResponseMessage> AddAsync(this ApiSession session, Guid productId, int quantity)
    {
        if (session.XsrfToken is null)
        {
            await session.StartAsync();
        }

        return await session.PostAsJsonAsync($"{CartUrl}/lines", new { productId, quantity });
    }

    /// <summary>Adds and expects success; returns the cart.</summary>
    public static async Task<CartDto> AddOkAsync(this ApiSession session, Guid productId, int quantity)
    {
        var response = await session.AddAsync(productId, quantity);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadAsync<CartDto>();
    }

    public static Task<HttpResponseMessage> SetQuantityAsync(this ApiSession session, Guid lineId, int quantity) =>
        session.PutAsJsonAsync($"{CartUrl}/lines/{lineId}", new { quantity });

    public static async Task<HttpResponseMessage> RemoveAsync(this ApiSession session, Guid lineId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{CartUrl}/lines/{lineId}");
        if (session.XsrfToken is { } token)
        {
            request.Headers.Add("X-XSRF-TOKEN", token);
        }

        return await session.Client.SendAsync(request, Ct);
    }

    /// <summary>Acknowledges one line's notices, or every line's when <paramref name="lineId"/> is null.</summary>
    public static Task<HttpResponseMessage> AcknowledgeAsync(this ApiSession session, Guid? lineId = null) =>
        session.PostAsJsonAsync(lineId is { } id ? $"{CartUrl}/lines/{id}/acknowledge" : $"{CartUrl}/acknowledge", new { });

    public static CartLineDto LineFor(this CartDto cart, Product product) =>
        cart.Lines.Should().ContainSingle(l => l.Product.Id == product.Id).Subject;

    /// <summary>An own product, listed and sellable, with a unique name (so catalogue tests never see it in their queries).</summary>
    public static async Task<Product> OwnProductAsync(this LumenApiFactory factory, decimal price = 18.50m, int onHand = 50)
    {
        var product = CatalogTestSupport.Own($"Cart test {Guid.NewGuid():N}", price: price, onHand: onHand);
        await factory.InsertAsync(product);
        return product;
    }

    /// <summary>A partner product. Remember it for <see cref="DeleteCartLinesAsync"/>.</summary>
    public static async Task<Product> PartnerProductAsync(
        this LumenApiFactory factory, string source, decimal price, int? quantity = 20, AvailabilityStatus whenUnknown = AvailabilityStatus.InStock)
    {
        var product = CatalogTestSupport.Partner(source, $"Cart test {Guid.NewGuid():N}", price: price, quantity: quantity, whenUnknown: whenUnknown);
        await factory.InsertAsync(product);
        return product;
    }

    /// <summary>Changes a product the way a sync or an admin edit would.</summary>
    public static Task UpdateProductAsync(this LumenApiFactory factory, Guid productId, Action<Product> change) =>
        factory.WithDbAsync(async db =>
        {
            var product = await db.Products.SingleAsync(p => p.Id == productId, Ct);
            change(product);
            await db.SaveChangesAsync(Ct);
        });

    public static Task DeleteCartLinesAsync(this LumenApiFactory factory, IReadOnlyCollection<Guid> productIds) =>
        factory.WithDbAsync(db => db.CartLines.Where(l => productIds.Contains(l.ProductId)).ExecuteDeleteAsync(CancellationToken.None));

    public static async Task<Guid> CustomerIdAsync(this LumenApiFactory factory, string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LumenDbContext>();
        return await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(Ct);
    }

    public static Task<bool> GuestCartExistsAsync(this LumenApiFactory factory, string token)
    {
        var hash = GuestCartToken.Hash(token);
        return factory.WithDbResultAsync(db => db.Carts.AnyAsync(c => c.AnonymousTokenHash == hash, Ct));
    }

    public static async Task<T> WithDbResultAsync<T>(this LumenApiFactory factory, Func<LumenDbContext, Task<T>> work)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<LumenDbContext>());
    }

    public static async Task<JsonElement> ShouldBeProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string? type = null)
    {
        response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync(Ct));
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var problem = await response.ReadJsonAsync();
        if (type is not null)
        {
            problem.GetProperty("type").GetString().Should().Be(type);
        }

        return problem;
    }
}
