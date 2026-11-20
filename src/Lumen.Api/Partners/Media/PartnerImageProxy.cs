using System.Collections.Concurrent;
using System.Net;
using Lumen.Api.Partners.Sync;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Partners.Media;

public enum ImageOutcome
{
    Found,

    /// <summary>No such image, or a URL Lumen refuses to fetch (not on the partner's own origin).</summary>
    NotFound,

    /// <summary>The partner did not deliver a usable image (down, error, not an image, too large).</summary>
    PartnerFailed,
}

public sealed record ImageResult(ImageOutcome Outcome, byte[]? Bytes = null, string? ContentType = null);

/// <summary>
/// Fetches partner images server-side, so the browser never calls a partner (partner images need the partner's
/// credentials and live on hosts the browser cannot reach). Goes through the partner's image client, so the
/// credentials and Harbor's image share of the rate limit apply. SSRF guard: only URLs on the partner's own origin or
/// its configured ImageOrigins are fetched, and redirects are never followed. Successes are cached (size-limited); failures briefly, so a down
/// partner costs one request per image per minute rather than one per page view.
/// </summary>
internal sealed class PartnerImageProxy(
    PartnerRegistry registry,
    IHttpClientFactory httpClients,
    IOptionsMonitor<MediaOptions> options,
    PartnerImageCache cache,
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
            return await fetch.Value.WaitAsync(options.CurrentValue.ResponseTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Typically a burst of Harbor images queued behind the shared rate limit. The fetch carries on and caches
            // the image, so the browser shows a placeholder now and the image on the next view.
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
                using var timeout = new CancellationTokenSource(settings.MaxFetchDuration);
                result = await FetchAsync(partner, url, settings.MaxImageBytes, timeout.Token);
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

    private async Task<ImageResult> FetchAsync(PartnerRegistration partner, Uri url, long maxBytes, CancellationToken cancellationToken)
    {
        var client = httpClients.CreateClient(partner.ImageHttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
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
