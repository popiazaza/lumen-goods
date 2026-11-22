using System.Collections.Concurrent;
using System.Net;
using Lumen.Api.Partners.Sync;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Polly.RateLimiting;

namespace Lumen.Api.Partners.Media;

public enum ImageOutcome
{
    Found,

    /// <summary>No such image, or a URL Lumen refuses to fetch (not on the partner's own origin).</summary>
    NotFound,

    /// <summary>The partner did not deliver a usable image (down, error, not an image, too large).</summary>
    PartnerFailed,

    /// <summary>
    /// The partner's image rate limit (Harbor's) had no token within <see cref="MediaOptions.RateLimitWait"/>, so the partner
    /// was not asked. Never cached: nothing is known about the image, and the next request may find a token.
    /// </summary>
    Throttled,
}

/// <param name="RetryAfter">For <see cref="ImageOutcome.Throttled"/>: when a token is expected, if the limiter said.</param>
public sealed record ImageResult(ImageOutcome Outcome, byte[]? Bytes = null, string? ContentType = null, TimeSpan? RetryAfter = null);

/// <summary>
/// Set by the image proxy on each partner image request, for a rate-limited image client (Harbor's) to read: how long the
/// request may wait for a rate-limit token before it is given up unsent.
/// </summary>
internal sealed record ImageTokenPolicy(TimeSpan MaxWait)
{
    public static readonly HttpRequestOptionsKey<ImageTokenPolicy> Key = new(nameof(ImageTokenPolicy));
}

/// <summary>
/// Fetches partner images server-side, so the browser never calls a partner (partner images need the partner's
/// credentials and live on hosts the browser cannot reach). Goes through the partner's image client, so the
/// credentials and Harbor's image share of the rate limit apply. SSRF guard: only URLs on the partner's own origin or
/// its configured ImageOrigins are fetched, and redirects are never followed. Successes are cached (size-limited); failures briefly, so a down
/// partner costs one request per image per minute rather than one per page view. A fetch waits only briefly for a
/// rate-limit token (<see cref="MediaOptions.RateLimitWait"/>) and is otherwise not sent, so queued image requests never
/// hold a browser's few connections; that answer is not cached.
/// </summary>
internal sealed class PartnerImageProxy(
    PartnerRegistry registry,
    IHttpClientFactory httpClients,
    IOptionsMonitor<MediaOptions> options,
    PartnerImageCache cache,
    TimeProvider time,
    ILogger<PartnerImageProxy> logger)
{
    public static readonly IReadOnlySet<string> AllowedContentTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/gif", "image/webp" };

    private static readonly ImageResult NotFound = new(ImageOutcome.NotFound);
    private static readonly ImageResult Failed = new(ImageOutcome.PartnerFailed);

    // Concurrent requests for the same uncached image share one partner call.
    private readonly ConcurrentDictionary<string, Lazy<Task<ImageResult>>> _inFlight = new(StringComparer.Ordinal);

    public async Task<ImageResult> GetAsync(string source, string sourceUrl, CancellationToken cancellationToken)
    {
        if (registry.Find(source) is not { } partner || !partner.Options().IsConfigured)
        {
            return NotFound;
        }

        // SSRF guard: only the partner's own origin and the image origins configured for it. Credentials still go to
        // the BaseUrl origin only (OriginBoundCredentialsHandler).
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var url)
            || !partner.Options().AllowedImageOrigins().Any(origin => PartnerUrls.IsSameOrigin(url, origin)))
        {
            logger.LogWarning("Refused to fetch {Partner} image outside its allowed origins: {Url}", partner.Code, sourceUrl);
            return NotFound;
        }

        var key = url.AbsoluteUri;
        if (cache.Get(key) is { } cached)
        {
            return cached;
        }

        var fetch = _inFlight.GetOrAdd(key, _ => new Lazy<Task<ImageResult>>(() => FetchAndCacheAsync(partner, url, key)));
        try
        {
            return await fetch.Value.WaitAsync(options.CurrentValue.ResponseTimeout, time, cancellationToken);
        }
        catch (TimeoutException)
        {
            // A slow partner. The fetch carries on and caches the image, so the browser shows a placeholder now and the
            // image on the next view.
            return Failed;
        }
    }

    private async Task<ImageResult> FetchAndCacheAsync(PartnerRegistration partner, Uri url, string key)
    {
        var settings = options.CurrentValue;
        try
        {
            ImageResult result;
            try
            {
                // Not tied to one browser request: other requests may be waiting for the same image.
                using var timeout = new CancellationTokenSource(settings.MaxFetchDuration, time);
                result = await FetchAsync(partner, url, settings, timeout.Token);
            }
            catch (Ports.PartnerException ex) when (ex.InnerException is RateLimiterRejectedException rejected)
            {
                // Lumen's own share of the partner's rate limit said no and the partner was never asked: not cached.
                return new ImageResult(ImageOutcome.Throttled, RetryAfter: rejected.RetryAfter);
            }
            catch (Exception ex) when (ex is Ports.PartnerException or OperationCanceledException or HttpRequestException or IOException)
            {
                logger.LogWarning("{Partner} image {Url} not fetched: {Message}", partner.Code, PartnerHttpClients.Describe(url), ex.Message);
                result = Failed;
            }

            cache.Set(key, result, result.Outcome == ImageOutcome.Found ? settings.CacheDuration : settings.FailureCacheDuration);
            return result;
        }
        finally
        {
            // After caching, so a request arriving now finds the cache entry rather than starting a second fetch.
            _inFlight.TryRemove(key, out _);
        }
    }

    private async Task<ImageResult> FetchAsync(PartnerRegistration partner, Uri url, MediaOptions settings, CancellationToken cancellationToken)
    {
        var maxBytes = settings.MaxImageBytes;
        var client = httpClients.CreateClient(partner.ImageHttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Options.Set(ImageTokenPolicy.Key, new ImageTokenPolicy(settings.RateLimitWait));
        using var response = await client.SendToPartnerAsync(request, HttpCompletionOption.ResponseHeadersRead, partner.Code, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            logger.LogWarning("{Partner} image {Url} returned {Status}", partner.Code, PartnerHttpClients.Describe(url), (int)response.StatusCode);
            return Failed;
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is null || !AllowedContentTypes.Contains(contentType))
        {
            logger.LogWarning("{Partner} image {Url} has content type {ContentType}, not an allowed image type", partner.Code, PartnerHttpClients.Describe(url), contentType);
            return Failed;
        }

        if (response.Content.Headers.ContentLength > maxBytes)
        {
            return Failed;
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                logger.LogWarning("{Partner} image {Url} is larger than {Max} bytes", partner.Code, PartnerHttpClients.Describe(url), maxBytes);
                return Failed;
            }

            buffer.Write(chunk, 0, read);
        }

        return new ImageResult(ImageOutcome.Found, buffer.ToArray(), contentType.ToLowerInvariant());
    }
}

/// <summary>
/// A dedicated, size-limited memory cache for partner images (a shared IMemoryCache with a size limit would force
/// every other user of it to declare entry sizes). Size is in bytes.
/// </summary>
internal sealed class PartnerImageCache(IOptions<MediaOptions> options) : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = options.Value.CacheSizeLimitBytes });

    public ImageResult? Get(string key) => _cache.TryGetValue(key, out ImageResult? result) ? result : null;

    public void Set(string key, ImageResult result, TimeSpan lifetime) =>
        _cache.Set(key, result, new MemoryCacheEntryOptions
        {
            Size = Math.Max(1, result.Bytes?.LongLength ?? 1),
            AbsoluteExpirationRelativeToNow = lifetime,
        });

    public void Dispose() => _cache.Dispose();
}
