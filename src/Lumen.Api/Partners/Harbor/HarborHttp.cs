using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Partners.Harbor;

/// <summary>
/// The two token buckets every Harbor request in this process draws from: one for the catalogue sync and orders, a
/// smaller one for images (see <see cref="HarborRateLimitOptions"/>).
/// </summary>
internal sealed class HarborRateGate : IDisposable
{
    public HarborRateGate(IOptions<HarborOptions> options)
    {
        var settings = options.Value.RateLimit;
        Limiter = Create(settings.RequestsPerMinute, settings.Burst, settings.QueueLimit);
        ImageLimiter = Create(settings.ImageRequestsPerMinute, settings.ImageBurst, settings.ImageQueueLimit);
    }

    /// <summary>Catalogue sync and order calls.</summary>
    public TokenBucketRateLimiter Limiter { get; }

    /// <summary>Image fetches only.</summary>
    public TokenBucketRateLimiter ImageLimiter { get; }

    public void Dispose()
    {
        Limiter.Dispose();
        ImageLimiter.Dispose();
    }

    private static TokenBucketRateLimiter Create(int perMinute, int burst, int queueLimit) => new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = burst,
        TokensPerPeriod = 1,
        ReplenishmentPeriod = TimeSpan.FromMinutes(1) / perMinute,
        QueueLimit = queueLimit,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true,
    });
}

/// <summary>X-Harbor-Key on requests to Harbor's origin only.</summary>
internal sealed class HarborKeyHandler(IOptionsMonitor<HarborOptions> options) : OriginBoundCredentialsHandler
{
    public const string HeaderName = "X-Harbor-Key";

    protected override Uri? PartnerOrigin => options.CurrentValue.BaseUri;

    protected override void AddCredentials(HttpRequestMessage request) =>
        request.Headers.TryAddWithoutValidation(HeaderName, options.CurrentValue.ApiKey);

    protected override void RemoveCredentials(HttpRequestMessage request) => request.Headers.Remove(HeaderName);
}
