using System.ComponentModel.DataAnnotations;

namespace Lumen.Api.Orders;

/// <summary>Partner order job settings (BR-ORD-01, BR-ORD-04). Bound from the <c>PartnerOrders</c> configuration section.</summary>
public sealed class PartnerOrdersOptions : IValidatableObject
{
    public const string SectionName = "PartnerOrders";

    /// <summary>How often an accepted Kestrel ticket is polled for its decision.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The first wait before resending a partner order that failed to send (timeout, 5xx, 429 without Retry-After);
    /// later waits grow from it. Every resend reuses the same reference.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>BR-ORD-04: a partner order not accepted this long after payment is given up and its line refunded.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "1.00:00:00")]
    public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>BR-ORD-04: an accepted partner order with no decision this long after acceptance needs an admin.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "30.00:00:00")]
    public TimeSpan NeedsAttentionAfter { get; init; } = TimeSpan.FromHours(24);

    /// <summary>The longest wait between two sends of a partner order that keeps failing (the waits double up to it).</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The longest wait between two polls of an accepted ticket that stays undecided (the waits grow up to it).</summary>
    [Range(typeof(TimeSpan), "00:00:01", "06:00:00")]
    public TimeSpan MaxPollInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a claimed partner order is reserved for the job instance calling its partner. If that instance dies
    /// mid-call, another one picks the order up after this; the resend reuses the same reference, so it is safe.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:30", "01:00:00")]
    public TimeSpan ClaimTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Partner orders one run claims at most; the rest wait for the next run a few seconds later.</summary>
    [Range(1, 10_000)]
    public int BatchSize { get; init; } = 20;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (PollInterval >= NeedsAttentionAfter)
        {
            yield return new ValidationResult("PollInterval must be shorter than NeedsAttentionAfter.", [nameof(PollInterval)]);
        }

        if (RetryDelay >= GiveUpAfter)
        {
            yield return new ValidationResult("RetryDelay must be shorter than GiveUpAfter, or nothing is ever retried.", [nameof(RetryDelay)]);
        }

        if (MaxRetryDelay < RetryDelay || MaxPollInterval < PollInterval)
        {
            yield return new ValidationResult("MaxRetryDelay and MaxPollInterval must not be shorter than RetryDelay and PollInterval.",
                [nameof(MaxRetryDelay), nameof(MaxPollInterval)]);
        }
    }
}

/// <summary>
/// Orders after Place order: the customer's order pages, partner orders (BR-ORD-01), refunds (BR-ORD-03), and the
/// admin orders pages (BR-ORD-04). Wired once in Program.cs.
/// </summary>
internal static class OrdersFeature
{
    public static IServiceCollection AddOrders(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PartnerOrdersOptions>()
            .Bind(configuration.GetSection(PartnerOrdersOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddPartnerOrderGateways();
        services.AddSingleton<SellerDirectory>();
        services.AddScoped<OrderDetailReader>();
        services.AddScoped<PartnerOrderProcessor>();

        // A singleton, so tests can call RunOnceAsync on the instance the host runs; started only when Jobs:Enabled.
        services.AddSingleton<PartnerOrderJob>();
        services.AddHostedService(sp => sp.GetRequiredService<PartnerOrderJob>());
        return services;
    }

    /// <summary>
    /// The customer's orders under /api/v1 (signed in; another customer's order is 404):
    /// <list type="bullet">
    /// <item><c>GET /orders?page=&amp;pageSize=</c> → <c>PagedResult&lt;</c><see cref="OrderSummaryDto"/><c>&gt;</c>, newest first</item>
    /// <item><c>GET /orders/{number}</c> → <see cref="OrderDetailDto"/></item>
    /// </list>
    /// </summary>
    public static RouteGroupBuilder MapOrderEndpoints(this RouteGroupBuilder api) => api.MapCustomerOrders();

    /// <summary>
    /// Admin orders under /api/v1/admin (admin role):
    /// <list type="bullet">
    /// <item><c>GET /admin/orders?status=&amp;from=&amp;to=&amp;page=&amp;pageSize=</c> → <c>PagedResult&lt;</c><see cref="AdminOrderSummaryDto"/><c>&gt;</c>;
    /// <c>status</c> in its snake_case wire form (e.g. <c>partially_refunded</c>, repeatable), parsed with the same
    /// naming policy as the JSON (enum binding from the query string would not accept the underscores); <c>from</c>/<c>to</c>
    /// as dates (the <c>CreatedUtc</c> index serves a date-only filter)</item>
    /// <item><c>GET /admin/orders/{number}</c> → <see cref="AdminOrderDto"/></item>
    /// <item><c>POST /admin/orders/{number}/lines/{lineId}/resolve</c> <see cref="ResolveOrderLineRequest"/> → <see cref="AdminOrderDto"/></item>
    /// </list>
    /// </summary>
    public static RouteGroupBuilder MapAdminOrderEndpoints(this RouteGroupBuilder api) => api.MapAdminOrders();
}
