using Lumen.Api.Infrastructure.Data;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Lumen.Api.Infrastructure;

internal static class HealthAndProxyExtensions
{
    public static IServiceCollection AddLumenHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");
        return services;
    }

    /// <summary>
    /// <c>/health</c>: ready (the database answers; compose waits for it). <c>/health/live</c>: the process serves
    /// requests, with no checks, so a database outage never gets a healthy API restarted.
    /// </summary>
    public static IEndpointRouteBuilder MapLumenHealthChecks(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        return app;
    }

    /// <summary>
    /// Every /api answer is personal (session, cart, orders, wallet) or may change at any moment (prices, stock), so no
    /// browser or proxy may store it. /media sets its own caching. The value is the one antiforgery sets when it issues
    /// a token, so it has nothing to override (and no warning to log) on the session and sign-in answers.
    /// </summary>
    public static IApplicationBuilder UseNoStoreForApi(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.CacheControl = "no-cache, no-store";
        }

        await next(context);
    });

    /// <summary>
    /// Browsers reach the API through the nginx container; the API publishes no host port. Forwarded headers
    /// are trusted only from private (RFC 1918) addresses, which is where Docker networks live. In production
    /// this list should be narrowed to the ingress proxy's range.
    /// </summary>
    public static IServiceCollection AddReverseProxySupport(this IServiceCollection services)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("10.0.0.0/8"));
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.16.0.0/12"));
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("192.168.0.0/16"));
        });
        return services;
    }

    private sealed class DatabaseHealthCheck(LumenDbContext db) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Cannot connect to the database.");
    }
}
