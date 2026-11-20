using System.Net;
using Lumen.Api.Partners.Mapping;
using Lumen.Api.Partners.Ports;
using Lumen.Domain.Catalog;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Partners.Kestrel;

/// <summary>
/// Kestrel's catalogue feed (<c>GET /feed/products.xml</c>): the whole catalogue in one document, no stock.
/// Conditional: sends the stored <c>Last-Modified</c> back verbatim as <c>If-Modified-Since</c>; a 304 means nothing to
/// do. The new Last-Modified becomes the watermark only once the engine has applied the feed, so a rejected feed is
/// fetched and judged again on the next run. No header → every run fetches unconditionally and the content hash decides.
/// </summary>
internal sealed class KestrelCatalogSource(
    IHttpClientFactory httpClients,
    IOptionsMonitor<KestrelOptions> options,
    IOptionsMonitor<PartnersOptions> partners,
    DescriptionSanitizer sanitizer) : IPartnerCatalogSource
{
    public const string HttpClientName = "partner-kestrel";

    private const int MaxWatermarkLength = 200;

    public string Partner => ProductSources.Kestrel;

    public PartnerCapabilities Capabilities { get; } = new(IncrementalChanges: false, StockInCatalogue: false);

    public async Task<CatalogFetchResult> FetchCatalogAsync(CatalogFetchRequest request, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var baseUri = settings.BaseUri ?? throw new PartnerException("Kestrel is not configured (Partners:Kestrel:BaseUrl).");
        var context = new PartnerMappingContext(baseUri, new CategoryMap(settings.CategoryMap), partners.CurrentValue.EffectiveSynonyms(), sanitizer);
        var client = httpClients.CreateClient(HttpClientName);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.FeedTimeout);

        using var message = new HttpRequestMessage(HttpMethod.Get, "feed/products.xml");
        if (!request.Full && !string.IsNullOrWhiteSpace(request.Watermark))
        {
            message.Headers.TryAddWithoutValidation("If-Modified-Since", request.Watermark);
        }

        try
        {
            using var response = await client.SendToPartnerAsync(message, HttpCompletionOption.ResponseHeadersRead, "Kestrel", timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return CatalogFetchResult.NotModified(request.Watermark);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                // Kestrel's error bodies differ per endpoint and may be empty: the status is what counts.
                throw new PartnerException(
                    $"Kestrel GET /feed/products.xml returned {(int)response.StatusCode} {response.ReasonPhrase}.", (int)response.StatusCode);
            }

            var lastModified = response.Content.Headers.TryGetValues("Last-Modified", out var values) ? values.FirstOrDefault()?.Trim() : null;
            if (lastModified?.Length > MaxWatermarkLength)
            {
                lastModified = null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var feed = await KestrelFeedParser.ParseAsync(body, settings.MaxFeedCharacters, context, timeout.Token);
            return new CatalogFetchResult(CatalogFetchKind.Complete, feed.Products, lastModified, feed.GeneratedUtc, feed.Problems);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PartnerException($"Kestrel feed took longer than {settings.FeedTimeout.TotalSeconds:0} s to download.", null, ex);
        }
        catch (IOException ex)
        {
            throw new PartnerException($"Kestrel feed download broke off: {ex.Message}", null, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new PartnerException($"Kestrel feed download failed: {ex.Message}", null, ex);
        }
    }
}
