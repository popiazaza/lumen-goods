using System.Threading.RateLimiting;
using Lumen.Api.Partners.Media;
using Microsoft.Extensions.Options;
using Polly;
using Polly.RateLimiting;

namespace Lumen.Api.Partners.Harbor;

/// <summary>
/// The two token buckets every Harbor request in this process draws from: one for the catalogue sync and orders, a
/// smaller one for images (see <see cref="HarborRateLimitOptions"/>).
/// </summary>
internal sealed class HarborRateGate : IDisposable
{
    private readonly TimeProvider _time;

    public HarborRateGate(IOptions<HarborOptions> options, TimeProvider time)
    {
        var settings = options.Value.RateLimit;
        _time = time;
        Limiter = Create(settings.RequestsPerMinute, settings.Burst, settings.QueueLimit);
        ImageLimiter = Create(settings.ImageRequestsPerMinute, settings.ImageBurst, settings.ImageQueueLimit);
    }

    /// <summary>Catalogue sync and order calls.</summary>
    public TokenBucketRateLimiter Limiter { get; }

    /// <summary>Image fetches only.</summary>
    public TokenBucketRateLimiter ImageLimiter { get; }

    /// <summary>
    /// How the image client's pipeline takes an <see cref="ImageLimiter"/> token, on every attempt (retries included).
    /// A request carrying an <see cref="ImageTokenPolicy"/> (the image proxy's) waits at most its MaxWait; a token that
    /// does not come in time gives a rejected lease with the limiter's Retry-After estimate, and the request is not sent.
    /// </summary>
    public async ValueTask<RateLimitLease> AcquireImageTokenAsync(RateLimiterArguments args)
    {
        var cancellationToken = args.Context.CancellationToken;
        var request = args.Context.GetRequestMessage();
        if (request is null || !request.Options.TryGetValue(ImageTokenPolicy.Key, out var policy))
        {
            return await ImageLimiter.AcquireAsync(1, cancellationToken);
        }

        using var timeout = new CancellationTokenSource(policy.MaxWait, _time);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await ImageLimiter.AcquireAsync(1, wait.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Out of time. One last try, which, when there is still no token, fails with the limiter's own Retry-After.
            return ImageLimiter.AttemptAcquire();
        }
    }

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
