using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Partners.Harbor;

/// <summary>The one token bucket every Harbor request in this process draws from (see <see cref="HarborRateLimitOptions"/>).</summary>
internal sealed class HarborRateGate : IDisposable
{
    public HarborRateGate(IOptions<HarborOptions> options)
    {
        var settings = options.Value.RateLimit;
        Limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = settings.Burst,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1) / settings.RequestsPerMinute,
            QueueLimit = settings.QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
    }

    public RateLimiter Limiter { get; }

    public void Dispose() => Limiter.Dispose();
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
