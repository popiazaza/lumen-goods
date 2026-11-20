using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Lumen.Api.Partners.Mapping;
using Lumen.Api.Partners.Ports;
using Lumen.Domain.Catalog;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Partners.Harbor;

/// <summary>
/// Harbor's catalogue (<c>GET /v2/catalog/items</c>) through the anti-corruption layer. Relies only on the contract:
/// - the cursor is opaque: passed back verbatim, never parsed or built; paging stops when next_cursor is null (or
///   missing/empty); a repeated cursor or too many pages abandons the pass;
/// - nothing is assumed about item order, page fullness (an empty page that is not the last is fine) or duplicates
///   (the latest copy of an item in a pass wins);
/// - incremental passes send <c>updated_since</c> = newest updated_at seen in earlier passes minus an overlap, in
///   the "Z" form, on every page; the watermark only moves after a complete pass (the engine stores it on success).
/// </summary>
internal sealed class HarborCatalogSource(
    IHttpClientFactory httpClients,
    IOptionsMonitor<HarborOptions> options,
    IOptionsMonitor<PartnersOptions> partners,
    DescriptionSanitizer sanitizer) : IPartnerCatalogSource
{
    public const string HttpClientName = "partner-harbor";

    public string Partner => ProductSources.Harbor;

    public PartnerCapabilities Capabilities { get; } = new(IncrementalChanges: true, StockInCatalogue: true);

    public async Task<CatalogFetchResult> FetchCatalogAsync(CatalogFetchRequest request, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var baseUri = settings.BaseUri ?? throw new PartnerException("Harbor is not configured (Partners:Harbor:BaseUrl).");
        var context = new PartnerMappingContext(baseUri, new CategoryMap(settings.CategoryMap), partners.CurrentValue.EffectiveSynonyms(), sanitizer);
        var client = httpClients.CreateClient(HttpClientName);

        var newestSeen = HarborWatermark.Parse(request.Watermark);
        var updatedSince = request.Full ? null : newestSeen - settings.WatermarkOverlap;

        var items = new Dictionary<string, PartnerProductSnapshot>(StringComparer.Ordinal);
        var problems = new List<string>();
        var cursorsSeen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        for (var page = 1; ; page++)
        {
            if (page > settings.MaxPagesPerPass)
            {
                throw new PartnerDataException($"Harbor catalogue pass abandoned after {settings.MaxPagesPerPass} pages without reaching the end.");
            }

            var (data, nextCursor) = await GetPageAsync(client, settings.PageSize, cursor, updatedSince, cancellationToken);
            foreach (var element in data)
            {
                var snapshot = ReadItem(element, context, problems);
                if (snapshot is null)
                {
                    continue;
                }

                // An item changed while we paged can appear twice; the later copy is the newer one.
                items[snapshot.SourceProductId] = snapshot;
                if (snapshot.UpdatedUtc is { } updated && (newestSeen is null || updated > newestSeen))
                {
                    newestSeen = updated;
                }
            }

            if (string.IsNullOrEmpty(nextCursor))
            {
                break;
            }

            if (!cursorsSeen.Add(nextCursor))
            {
                throw new PartnerDataException("Harbor catalogue pass abandoned: next_cursor repeated a cursor already used in this pass.");
            }

            cursor = nextCursor;
        }

        return new CatalogFetchResult(
            request.Full ? CatalogFetchKind.Complete : CatalogFetchKind.Changes,
            [.. items.Values],
            HarborWatermark.Format(newestSeen),
            ChangedAtUtc: null,
            problems);
    }

    private static PartnerProductSnapshot? ReadItem(JsonElement element, PartnerMappingContext context, List<string> problems)
    {
        HarborItemDto? dto;
        try
        {
            dto = element.Deserialize<HarborItemDto>(HarborJson.Options);
        }
        catch (JsonException ex)
        {
            problems.Add($"Harbor item skipped: not in the contract's shape ({ex.Message}).");
            return null;
        }

        if (dto is null)
        {
            problems.Add("Harbor item skipped: null entry in data.");
            return null;
        }

        var snapshot = HarborItemMapper.Map(dto, context, out var problem);
        if (problem is not null)
        {
            problems.Add(problem);
        }

        return snapshot;
    }

    private static async Task<(List<JsonElement> Data, string? NextCursor)> GetPageAsync(
        HttpClient client, int pageSize, string? cursor, DateTimeOffset? updatedSince, CancellationToken cancellationToken)
    {
        var url = new StringBuilder("v2/catalog/items?limit=").Append(pageSize.ToString(CultureInfo.InvariantCulture));
        if (cursor is not null)
        {
            url.Append("&cursor=").Append(Uri.EscapeDataString(cursor));
        }

        if (updatedSince is { } since)
        {
            url.Append("&updated_since=").Append(Uri.EscapeDataString(HarborWatermark.ToQueryValue(since)));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url.ToString());
        using var response = await client.SendToPartnerAsync(request, HttpCompletionOption.ResponseContentRead, "Harbor", cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await HarborErrors.ToExceptionAsync(response, $"GET {PartnerHttpClients.Describe(request.RequestUri)}", cancellationToken);
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new PartnerDataException("Harbor catalogue page has no data array.");
            }

            string? next = null;
            if (root.TryGetProperty("next_cursor", out var nextCursor))
            {
                next = nextCursor.ValueKind switch
                {
                    JsonValueKind.String => nextCursor.GetString(),
                    JsonValueKind.Null => null,
                    _ => throw new PartnerDataException($"Harbor next_cursor is a {nextCursor.ValueKind}, not a string."),
                };
            }

            // Clone: the elements must outlive the document.
            return ([.. data.EnumerateArray().Select(e => e.Clone())], next);
        }
        catch (JsonException ex)
        {
            throw new PartnerDataException($"Harbor catalogue page is not valid JSON: {ex.Message}", ex);
        }
    }
}

/// <summary>Harbor's standard error body <c>{"error":{"code","message"}}</c>, read defensively (it may be missing or HTML).</summary>
internal static class HarborErrors
{
    public static async Task<PartnerException> ToExceptionAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var body = await PartnerHttpClients.ReadSnippetAsync(response, 2000, cancellationToken);
        string? code = null;
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            }
        }
        catch (JsonException)
        {
            // Not the standard body (a proxy page, an empty body): the status says enough.
        }

        var detail = code is null ? "" : $" ({code}{(message is null ? "" : $": {message}")})";
        return new PartnerException($"Harbor {what} returned {status} {response.ReasonPhrase}{detail}.", status);
    }
}

/// <summary>The Harbor watermark: the newest <c>updated_at</c> seen in a completed pass, stored as ISO 8601 UTC.</summary>
internal static class HarborWatermark
{
    public static DateTimeOffset? Parse(string? watermark) =>
        DateTimeOffset.TryParse(watermark, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : null;

    public static string? Format(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Whole seconds with "Z" (the contract's "ISO 8601 UTC"); truncating moves it earlier, which is the safe side.</summary>
    public static string ToQueryValue(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
