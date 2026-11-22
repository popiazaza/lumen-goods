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
    private readonly int _imageBurst;
    private readonly TimeSpan _imagePeriod;

    public HarborRateGate(IOptions<HarborOptions> options, TimeProvider time)
    {
        var settings = options.Value.RateLimit;
        _time = time;
        _imageBurst = settings.ImageBurst;
        _imagePeriod = TimeSpan.FromMinutes(1) / settings.ImageRequestsPerMinute;
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
    /// A warm-up request (SpareOnly) never waits and takes a token only from a full bucket, so browsers keep the burst.
    /// </summary>
    public async ValueTask<RateLimitLease> AcquireImageTokenAsync(RateLimiterArguments args)
    {
        var cancellationToken = args.Context.CancellationToken;
        var request = args.Context.GetRequestMessage();
        if (request is null || !request.Options.TryGetValue(ImageTokenPolicy.Key, out var policy))
        {
            return await ImageLimiter.AcquireAsync(1, cancellationToken);
        }

        if (policy.SpareOnly)
        {
            return ImageLimiter.GetStatistics()?.CurrentAvailablePermits >= _imageBurst ? ImageLimiter.AttemptAcquire() : new NoSpareToken(_imagePeriod);
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

    /// <summary>A refused lease that says when to look again: after one more token, the bucket may be full.</summary>
    private sealed class NoSpareToken(TimeSpan retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = metadataName == MetadataName.RetryAfter.Name ? retryAfter : null;
            return metadata is not null;
        }
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
