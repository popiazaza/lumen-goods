using System.ComponentModel.DataAnnotations;
using Lumen.Domain.Orders;
using Lumen.Domain.Pricing;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Checkout;

/// <summary>Checkout settings. Bound from the <c>Checkout</c> configuration section.</summary>
public sealed class CheckoutOptions
{
    public const string SectionName = "Checkout";

    /// <summary>The one country Lumen ships to at launch (A-51), ISO 3166-1 alpha-2. Addresses use its rules.</summary>
    [Required, RegularExpression("^[A-Z]{2}$")]
    public string Country { get; init; } = "US";

    /// <summary>
    /// BR-CHK-02: how long Place order waits for a partner's live price and stock check. A partner that does not
    /// answer in time makes its lines "Temporarily unavailable"; nothing is created.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:00:30")]
    public TimeSpan PartnerCheckTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Checkout: pricing (tax and shipping, BR-MONEY-02, BR-SHIP-01), the preview, and Place order (BR-CHK-01, BR-CHK-02,
/// BR-STOCK-01, BR-PAY-01 (a)), with the partners' live availability checks. Wired once in Program.cs.
/// </summary>
internal static class CheckoutFeature
{
    public static IServiceCollection AddCheckout(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CheckoutOptions>()
            .Bind(configuration.GetSection(CheckoutOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<PricingOptions>()
            .Bind(configuration.GetSection(PricingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The cart's totals preview, the checkout preview and Place order all price with this one service.
        services.AddSingleton(sp => new PricingService(sp.GetRequiredService<IOptions<PricingOptions>>().Value));
        services.AddSingleton(_ => new OrderNumberGenerator());

        services.AddPartnerAvailabilityChecks();
        services.AddScoped<CheckoutCart>();
        services.AddScoped<CheckoutPreviewService>();
        services.AddScoped<PlaceOrderService>();
        services.AddScoped<OrderDetailMapper>();
        return services;
    }

    /// <summary>
    /// Checkout endpoints under /api/v1 (signed-in customers only):
    /// <list type="bullet">
    /// <item><c>GET /checkout/preview?shippingMethod=standard|express</c> → <see cref="CheckoutPreviewDto"/></item>
    /// <item><c>POST /checkout/orders</c> <see cref="PlaceOrderRequest"/> → 201 <c>OrderDetailDto</c> (Location
    /// <c>/api/v1/orders/{number}</c>); 200 with the same order for a repeated checkout key; 409
    /// <see cref="PriceChangedProblem"/>; 400 validation problem</item>
    /// </list>
    /// </summary>
    public static RouteGroupBuilder MapCheckoutEndpoints(this RouteGroupBuilder api) => api.MapCheckout();
}
