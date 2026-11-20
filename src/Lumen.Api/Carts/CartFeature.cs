using System.ComponentModel.DataAnnotations;

namespace Lumen.Api.Carts;

/// <summary>Cart settings. Bound from the <c>Cart</c> configuration section.</summary>
public sealed class CartOptions
{
    public const string SectionName = "Cart";

    /// <summary>
    /// How long a guest cart and its cookie live without being used (BR-CART-01, A-48). Sliding: every change, and a
    /// visit at least a day after the last renewal, starts the period again.
    /// </summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "365.00:00:00")]
    public TimeSpan GuestCartLifetime { get; init; } = TimeSpan.FromDays(30);

    /// <summary>How often <see cref="GuestCartCleanupJob"/> deletes guest carts past their lifetime.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan GuestCartCleanupInterval { get; init; } = TimeSpan.FromHours(1);
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
    /// no guest cart changes nothing, so a replayed sign-in cannot merge twice. Only for accounts with the customer role:
    /// staff accounts have no cart, so for them nothing changes. A failure leaves the guest cart and cookie as they were.
    /// </summary>
    Task MergeGuestCartAsync(HttpContext httpContext, Guid customerId, CancellationToken cancellationToken);
}

/// <summary>
/// The cart (BR-CART-01..03): guest carts identified by a cookie, customers' saved carts, change notices, the merge on
/// sign-in, and the clean-up of expired guest carts. Wired once in Program.cs.
/// </summary>
internal static class CartFeature
{
    public static IServiceCollection AddCart(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CartOptions>()
            .Bind(configuration.GetSection(CartOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<GuestCartCookie>();
        services.AddScoped<CartService>();
        services.AddScoped<CartPresenter>();
        services.AddScoped<ICartMergeService, CartMergeService>();
        services.AddSingleton<GuestCartCleanupJob>();
        services.AddHostedService(sp => sp.GetRequiredService<GuestCartCleanupJob>());
        return services;
    }

    /// <summary>
    /// Cart endpoints under /api/v1, for guests and customers alike; 403 for a signed-in account without the customer
    /// role (the admin). Every one answers 200 with the whole <see cref="CartDto"/>, or 409
    /// <see cref="CartProblems.CartChangedType"/> when the guest cart the cookie names is gone (merged by a sign-in racing
    /// the request: re-read the cart):
    /// <list type="bullet">
    /// <item><c>GET /cart</c> (an empty cart when there is none; never creates one)</item>
    /// <item><c>POST /cart/lines</c> <see cref="AddCartLineRequest"/> (creates the cart, and for a guest the cookie, on
    /// first add); 400 quantity not 1 to 10; 422 <see cref="CartProblems"/></item>
    /// <item><c>PUT /cart/lines/{lineId}</c> <see cref="UpdateCartLineRequest"/>; 404 not your line; 422 raising past the limit</item>
    /// <item><c>DELETE /cart/lines/{lineId}</c>; 404 not your line</item>
    /// <item><c>POST /cart/lines/{lineId}/acknowledge</c>: accept the line's notices (seen price and availability := current); 404 not your line</item>
    /// <item><c>POST /cart/acknowledge</c>: accept every line's notices</item>
    /// </list>
    /// </summary>
    public static RouteGroupBuilder MapCartEndpoints(this RouteGroupBuilder api) => api.MapCartRoutes();
}
