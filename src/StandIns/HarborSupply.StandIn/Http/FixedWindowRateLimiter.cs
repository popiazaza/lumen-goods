using Microsoft.Extensions.Options;

namespace HarborSupply.StandIn.Http;

/// <summary>
/// "60 requests per minute per key" as a fixed window: a key's window opens with its first request, admits
/// <see cref="HarborOptions.RequestsPerMinute"/> requests and closes 60 seconds later.
/// Hand-written rather than ASP.NET Core's limiter because that one runs on the real clock and tests use a fake one.
/// </summary>
internal sealed class FixedWindowRateLimiter(TimeProvider clock, IOptions<HarborOptions> options)
{
    private static readonly TimeSpan WindowLength = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);

    /// <summary>Counts the request and returns null, or returns the time left until the key's window resets.</summary>
    public TimeSpan? TryAcquire(string key)
    {
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            if (!_windows.TryGetValue(key, out var window) || now >= window.Start + WindowLength)
            {
                window = new Window(now, 0);
            }

            if (window.Count >= options.Value.RequestsPerMinute)
            {
                return window.Start + WindowLength - now;
            }

            _windows[key] = window with { Count = window.Count + 1 };
            return null;
        }
    }

    private readonly record struct Window(DateTimeOffset Start, int Count);
}
