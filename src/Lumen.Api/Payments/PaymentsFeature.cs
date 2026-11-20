using Lumen.Domain.Payments;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Payments;

/// <summary>
/// Payments (BR-PAY-01..07): card payments through the in-process mock gateway, wallet payments, one-time codes, the
/// payment window. Wired once in Program.cs; the payments work fills in the endpoints, the gateway mock and the jobs.
/// </summary>
internal static class PaymentsFeature
{
    public static IServiceCollection AddPayments(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PaymentOptions>()
            .Bind(configuration.GetSection(PaymentOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(sp => new PaymentWindowPolicy(sp.GetRequiredService<IOptions<PaymentOptions>>().Value));
        services.AddSingleton(sp => new OneTimeCodePolicy(sp.GetRequiredService<IOptions<PaymentOptions>>().Value));
        return services;
    }

    /// <summary>
    /// Payment endpoints under /api/v1 (signed-in customers, their own orders only; another customer's order is 404):
    /// <list type="bullet">
    /// <item><c>POST /orders/{number}/payments</c> <see cref="PayRequest"/> → 202 <see cref="PaymentStatusDto"/>;
    /// after a Failed payment it retries that same payment id (BR-PAY-05)</item>
    /// <item><c>GET /orders/{number}/payments/current</c> → <see cref="PaymentStatusDto"/> (404 before any payment)</item>
    /// <item><c>POST /orders/{number}/payments/current/one-time-code</c> <see cref="OneTimeCodeRequest"/> → <see cref="PaymentStatusDto"/></item>
    /// </list>
    /// </summary>
    public static RouteGroupBuilder MapPaymentEndpoints(this RouteGroupBuilder api) => api;
}
