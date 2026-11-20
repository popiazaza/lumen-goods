using Lumen.Domain.Payments;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Payments;

/// <summary>
/// Payments (BR-PAY-01..07): card payments through the in-process mock gateway, wallet payments, one-time codes, the
/// payment window job and the refund job. Wired once in Program.cs.
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
        services.AddOptions<PaymentJobsOptions>()
            .Bind(configuration.GetSection(PaymentJobsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The in-process mock card gateway (BR-PAY-02); a real gateway would replace only this registration.
        services.AddSingleton<IPaymentGateway, MockPaymentGateway>();

        services.AddScoped<OrderPaymentWriter>();
        services.AddScoped<PaymentRecorder>();
        services.AddScoped<PaymentService>();
        services.AddScoped<CardChargeProcess>();
        services.AddScoped<PaymentHousekeeping>();
        services.AddScoped<PaymentStatusReader>();
        services.AddScoped<RefundExecutor>();

        // Singletons so tests and shutdown can reach them; hosted so they run (each loop honours Jobs:Enabled).
        services.AddSingleton<DetachedPaymentRunner>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DetachedPaymentRunner>());
        services.AddSingleton<PaymentWindowJob>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<PaymentWindowJob>());
        services.AddSingleton<RefundJob>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RefundJob>());
        return services;
    }

    /// <summary>
    /// Payment endpoints under /api/v1 (signed-in customers, their own orders only; another customer's order is 404):
    /// <list type="bullet">
    /// <item><c>POST /orders/{number}/payments</c> <see cref="PayRequest"/> → 202 <see cref="PaymentStatusDto"/>; 409
    /// <see cref="PaymentInProgressProblem"/> while one runs</item>
    /// <item><c>GET /orders/{number}/payments/current</c> → <see cref="PaymentStatusDto"/> (404 before any payment)</item>
    /// <item><c>POST /orders/{number}/payments/current/one-time-code</c> <see cref="OneTimeCodeRequest"/> → <see cref="PaymentStatusDto"/></item>
    /// </list>
    /// <para>
    /// Failed payments (BR-PAY-05): at most one payment per order is not final (a partial unique index), and Failed is
    /// not final, so a Failed payment is settled before anything else starts: Pay first asks the gateway for its status;
    /// charged → Succeeded (order Paid), never charged → Declined with <c>GatewayErrorUnresolvedNotCharged</c> (final;
    /// the failed attempt was already counted, so it is not counted again), unknown → only the same payment id may be
    /// tried again (<see cref="PaymentRetryMode"/>).
    /// </para>
    /// <para>
    /// Housekeeping (owned by the payments work, in the hold-expiry job): payments stuck in Created or Processing past a
    /// threshold (the gateway call was lost, e.g. a restart mid-charge), and Failed payments of orders whose hold has
    /// ended, are reconciled through the gateway's status for their payment id, found with the
    /// <c>(Status, StatusChangedUtc)</c> index: charged → Succeeded and the order Paid (even after the hold, like a slow
    /// card); not charged → closed as Declined (<c>GatewayErrorUnresolvedNotCharged</c>) along the transition table and the
    /// order moves on (<see cref="PaymentWindowPolicy"/>).
    /// No order is expired while such a payment could still have charged.
    /// </para>
    /// </summary>
    public static RouteGroupBuilder MapPaymentEndpoints(this RouteGroupBuilder api) => api.MapPayments();
}
