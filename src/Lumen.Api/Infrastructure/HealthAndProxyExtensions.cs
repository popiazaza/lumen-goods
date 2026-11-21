using System.ComponentModel.DataAnnotations;
using System.Net;
using Lumen.Api.Infrastructure.Data;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

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
    /// Browsers reach the API through the nginx container, which overwrites X-Forwarded-For with the client's address.
    /// Forwarded headers are believed only from <see cref="TrustedProxyOptions.KnownProxies"/> (docker compose: nginx's
    /// fixed address) and loopback. Any other peer, such as another container on the network or a call to the port the
    /// dev override publishes, is taken as it arrives, so it cannot choose its own per-IP rate limit.
    /// </summary>
    public static IServiceCollection AddReverseProxySupport(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TrustedProxyOptions>()
            .Bind(configuration.GetSection(TrustedProxyOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<TrustedProxyOptions>>((options, proxies) =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (var proxy in proxies.Value.KnownProxies)
            {
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            }
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

/// <summary>The reverse proxies whose forwarded headers the API believes (section <c>ForwardedHeaders</c>).</summary>
public sealed class TrustedProxyOptions : IValidatableObject
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>
    /// The proxies' own IP addresses (docker compose: the nginx container's). Empty: only loopback is believed, and the
    /// in-process test server, which has no peer address, is not affected.
    /// </summary>
    public List<string> KnownProxies { get; init; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        KnownProxies.Where(proxy => !IPAddress.TryParse(proxy, out _))
            .Select(proxy => new ValidationResult($"KnownProxies: '{proxy}' is not an IP address.", [nameof(KnownProxies)]));
}
