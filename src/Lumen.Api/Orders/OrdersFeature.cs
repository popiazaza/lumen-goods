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
    }
}

/// <summary>
/// Orders after Place order: the customer's order pages, partner orders (BR-ORD-01), refunds (BR-ORD-03), and the
/// admin orders pages (BR-ORD-04). Wired once in Program.cs; the orders work fills in the endpoints and the jobs.
/// </summary>
internal static class OrdersFeature
{
    public static IServiceCollection AddOrders(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PartnerOrdersOptions>()
            .Bind(configuration.GetSection(PartnerOrdersOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        return services;
    }

    /// <summary>
    /// The customer's orders under /api/v1 (signed in; another customer's order is 404):
    /// <list type="bullet">
    /// <item><c>GET /orders?page=&amp;pageSize=</c> → <c>PagedResult&lt;</c><see cref="OrderSummaryDto"/><c>&gt;</c>, newest first</item>
    /// <item><c>GET /orders/{number}</c> → <see cref="OrderDetailDto"/></item>
    /// </list>
    /// </summary>
    public static RouteGroupBuilder MapOrderEndpoints(this RouteGroupBuilder api) => api;

    /// <summary>
    /// Admin orders under /api/v1/admin (admin role):
    /// <list type="bullet">
    /// <item><c>GET /admin/orders?status=&amp;from=&amp;to=&amp;page=&amp;pageSize=</c> → <c>PagedResult&lt;</c><see cref="AdminOrderSummaryDto"/><c>&gt;</c></item>
    /// <item><c>GET /admin/orders/{number}</c> → <see cref="AdminOrderDto"/></item>
    /// <item><c>POST /admin/orders/{number}/lines/{lineId}/resolve</c> <see cref="ResolveOrderLineRequest"/> → <see cref="AdminOrderDto"/></item>
    /// </list>
    /// </summary>
    public static RouteGroupBuilder MapAdminOrderEndpoints(this RouteGroupBuilder api) => api;
}
