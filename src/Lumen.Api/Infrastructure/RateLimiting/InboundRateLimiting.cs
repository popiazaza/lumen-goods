using System.Globalization;
using System.Threading.RateLimiting;
using Lumen.Api.Checkout;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Infrastructure.RateLimiting;

/// <summary>The inbound rate-limit policies, for <c>RequireRateLimiting</c> on the endpoints they protect.</summary>
internal static class RateLimitPolicies
{
    public const string SignIn = "sign-in";
    public const string Register = "register";
    public const string PlaceOrder = "place-order";
    public const string Payment = "payment";
}

public static class RateLimitProblemTypes
{
    /// <summary>429 from a rate limit (not the account lockout, which has no type): wait <c>Retry-After</c> seconds.</summary>
    public const string RateLimited = "urn:lumen:problem:rate-limited";
}

/// <summary>
/// Inbound rate limiting (<see cref="RateLimitingOptions"/>): sign-in and registration per client IP, Place order and
/// payment starts per signed-in customer. A rejected request gets a 429 problem with <c>Retry-After</c>. The limiters
/// keep their own clock (System.Threading.RateLimiting takes no <see cref="TimeProvider"/>); tests only ever exceed a
/// limit, they never wait for a window to pass.
/// </summary>
internal static class InboundRateLimiting
{
    public static IServiceCollection AddLumenRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<SignInGate>();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, _) =>
                await TooManyRequests(context.HttpContext, context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : null)
                    .ExecuteAsync(context.HttpContext);
            options.AddPolicy(RateLimitPolicies.SignIn, http => PerClient(http, o => o.SignInPerClient));
            options.AddPolicy(RateLimitPolicies.Register, http => PerClient(http, o => o.RegisterPerClient));
            options.AddPolicy(RateLimitPolicies.PlaceOrder, http => PerCustomer(http, o => o.PlaceOrderPerCustomer));
            options.AddPolicy(RateLimitPolicies.Payment, http => PerCustomer(http, o => o.PaymentPerCustomer));
        });
        return services;
    }

    /// <summary>The 429 every limit answers with: a problem, and <c>Retry-After</c> in whole seconds (at least 1).</summary>
    public static ProblemHttpResult TooManyRequests(HttpContext http, TimeSpan? retryAfter)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter?.TotalSeconds ?? 1));
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return TypedResults.Problem(statusCode: StatusCodes.Status429TooManyRequests, type: RateLimitProblemTypes.RateLimited,
            title: "Too many attempts", detail: $"Wait {seconds} seconds, then try again.");
    }

    /// <summary>
    /// Keyed by the client IP, which UseForwardedHeaders has already taken from nginx's X-Forwarded-For (nginx overwrites
    /// any value the client sent). Kestrel always knows the peer address; only the in-process test server has none, and
    /// that is not limited, so the tests that need a client set X-Forwarded-For.
    /// </summary>
    private static RateLimitPartition<string> PerClient(HttpContext http, Func<RateLimitingOptions, WindowLimit> limit) =>
        http.Connection.RemoteIpAddress is { } ip
            ? FixedWindow(ip.ToString(), http, limit)
            : RateLimitPartition.GetNoLimiter("no-client-address");

    /// <summary>Keyed by the signed-in customer. These endpoints require sign-in: without one, authorization has already answered 401.</summary>
    private static RateLimitPartition<string> PerCustomer(HttpContext http, Func<RateLimitingOptions, WindowLimit> limit) =>
        http.User.CustomerId() is { } customerId
            ? FixedWindow(customerId.ToString(), http, limit)
            : RateLimitPartition.GetNoLimiter("anonymous");

    private static RateLimitPartition<string> FixedWindow(string key, HttpContext http, Func<RateLimitingOptions, WindowLimit> limit)
    {
        var window = limit(http.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value);
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = window.PermitLimit,
            Window = window.Window,
            QueueLimit = 0,
        });
    }
}

/// <summary>
/// Sign-in attempts for one email run one at a time; up to <see cref="RateLimitingOptions.SignInQueuePerEmail"/> more wait
/// their turn and the rest get 429. Identity's lockout reads the failed count, adds one and saves it with a concurrency
/// check, so wrong passwords checked at the same moment would count once (the other saves fail and are not retried) and
/// a burst of guesses could stay under the 5-attempt lockout. One at a time, every attempt counts. This is a lock inside
/// one API process; more than one instance would need it in the database.
/// </summary>
internal sealed class SignInGate : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public SignInGate(IOptions<RateLimitingOptions> options)
    {
        var queue = options.Value.SignInQueuePerEmail;
        // Idle partitions are removed by the limiter itself, so one per email ever tried does not pile up.
        _limiter = PartitionedRateLimiter.Create<string, string>(email => RateLimitPartition.GetConcurrencyLimiter(email,
            _ => new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = queue, QueueProcessingOrder = QueueProcessingOrder.OldestFirst }));
    }

    /// <param name="normalizedEmail">Identity's normalized email, so case variants of one address share a turn.</param>
    public ValueTask<RateLimitLease> WaitTurnAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        _limiter.AcquireAsync(normalizedEmail, 1, cancellationToken);

    public void Dispose() => _limiter.Dispose();
}
