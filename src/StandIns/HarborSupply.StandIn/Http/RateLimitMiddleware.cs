using System.Globalization;

namespace HarborSupply.StandIn.Http;

/// <summary>
/// Contract: over the limit, 429 with a Retry-After header in seconds. Runs after <see cref="ApiKeyMiddleware"/>,
/// so requests rejected with 401 are never counted, and every authenticated request (images too) is.
/// </summary>
internal sealed class RateLimitMiddleware(RequestDelegate next, FixedWindowRateLimiter limiter)
{
    public Task InvokeAsync(HttpContext context)
    {
        var key = context.Request.Headers[ApiKeyMiddleware.HeaderName].ToString();
        if (limiter.TryAcquire(key) is { } wait)
        {
            // Whole seconds, rounded up so a client that waits exactly this long lands in the next window.
            var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
            context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return HarborErrors.WriteAsync(context, StatusCodes.Status429TooManyRequests, "RATE_LIMITED", $"Rate limit exceeded. Retry after {seconds} seconds.");
        }

        return next(context);
    }
}
