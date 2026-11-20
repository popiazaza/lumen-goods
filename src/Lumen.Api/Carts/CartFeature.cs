using System.ComponentModel.DataAnnotations;

namespace Lumen.Api.Carts;

/// <summary>Cart settings. Bound from the <c>Cart</c> configuration section.</summary>
public sealed class CartOptions
{
    public const string SectionName = "Cart";

    /// <summary>How long a guest cart and its cookie live without a change (BR-CART-01, A-48).</summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "365.00:00:00")]
    public TimeSpan GuestCartLifetime { get; init; } = TimeSpan.FromDays(30);
}

/// <summary>
/// BR-CART-01: merges a guest's cart into the customer's saved cart. Sign-in and registration call it right after the
/// session cookie is issued (<c>AuthEndpoints</c>), so the cart feature owns everything about carts and cookies.
/// </summary>
public interface ICartMergeService
{
    /// <summary>
    /// Merges the guest cart named by the request's <see cref="GuestCartToken.CookieName"/> cookie into the customer's
    /// saved cart (creating it if needed): same product → quantities summed, capped at the known available quantity and
    /// 10, with a quantity-limited notice. Then deletes the guest cart and expires the cookie. Idempotent: no cookie or
    /// no guest cart changes nothing, so a replayed sign-in cannot merge twice.
    /// </summary>
    Task MergeGuestCartAsync(HttpContext httpContext, Guid customerId, CancellationToken cancellationToken);
}

/// <summary>Placeholder until the cart feature implements the merge.</summary>
internal sealed class NoOpCartMergeService : ICartMergeService
{
    public Task MergeGuestCartAsync(HttpContext httpContext, Guid customerId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The cart (BR-CART-01..03): guest carts identified by a cookie, customers' saved carts, change notices.
/// Wired once in Program.cs; the cart work fills these in.
/// </summary>
internal static class CartFeature
{
    public static IServiceCollection AddCart(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CartOptions>()
            .Bind(configuration.GetSection(CartOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<ICartMergeService, NoOpCartMergeService>();
        return services;
    }

    /// <summary>
    /// Cart endpoints under /api/v1, for guests and customers alike (every one answers with the whole <see cref="CartDto"/>):
    /// <list type="bullet">
    /// <item><c>GET /cart</c></item>
    /// <item><c>POST /cart/lines</c> <see cref="AddCartLineRequest"/> (creates the cart, and for a guest the cookie, on first add)</item>
    /// <item><c>PUT /cart/lines/{lineId}</c> <see cref="UpdateCartLineRequest"/></item>
    /// <item><c>DELETE /cart/lines/{lineId}</c></item>
    /// <item><c>POST /cart/lines/{lineId}/acknowledge</c>: accept the line's notices (seen price and availability := current)</item>
    /// </list>
    /// </summary>
    public static RouteGroupBuilder MapCartEndpoints(this RouteGroupBuilder api) => api;
}
