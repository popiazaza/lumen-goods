using System.Net;
using System.Text.Json;
using Lumen.Api.Partners.Mapping;
using Lumen.Api.Partners.Ports;
using Lumen.Domain.Catalog;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Partners.Harbor;

/// <summary>
/// BR-CHK-02: Harbor's live check before an order is created: <c>GET /v2/catalog/items/{item_code}</c> per line, through
/// the same named client as the sync (key, shared rate limiter, resilience), mapped with the sync's own mapper so the
/// check and the catalogue can never disagree about what an item means.
/// - 404 ITEM_NOT_FOUND, discontinued, no valid price, unmapped category, SOLD_OUT or on hand 0 → Unavailable.
/// - LIMITED with fewer on hand than the line asks for → Short with the number.
/// - A different price (up or down) → PriceChanged with the live USD price.
/// - Anything else (429, 5xx, timeout, unreadable body) → Unreachable: never sold without evidence.
/// - Lines beyond <see cref="HarborOptions.MaxLiveChecks"/> → NotChecked, not sent: Place order then decides on the
///   freshness of Lumen's synced data.
/// </summary>
internal sealed class HarborAvailabilityCheck(
    IHttpClientFactory httpClients,
    IOptionsMonitor<HarborOptions> options,
    IOptionsMonitor<PartnersOptions> partners,
    DescriptionSanitizer sanitizer,
    CurrencyConverter currency,
    ILogger<HarborAvailabilityCheck> logger) : IPartnerAvailabilityCheck
{
    public string Partner => ProductSources.Harbor;

    public async Task<IReadOnlyList<PartnerLineCheckResult>> CheckAsync(IReadOnlyList<PartnerLineCheck> lines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var settings = options.CurrentValue;
        if (settings.BaseUri is not { } baseUri)
        {
            return [.. lines.Select(l => new PartnerLineCheckResult(l.LineId, PartnerLineCheckOutcome.Unreachable, Detail: "Harbor is not configured."))];
        }

        var context = new PartnerMappingContext(baseUri, new CategoryMap(settings.CategoryMap), partners.CurrentValue.EffectiveSynonyms(), sanitizer);
        var client = httpClients.CreateClient(HarborCatalogSource.HttpClientName);

        // Harbor's rate limit leaves room for only so many calls in Place order's time limit, so the lines most likely
        // to have run short are asked first (a known quantity, tightest margin first) and the rest are not sent at all.
        var ordered = lines.OrderBy(TightnessRank).ToList();
        var asked = ordered.Take(settings.MaxLiveChecks).ToList();
        var notAsked = ordered.Skip(settings.MaxLiveChecks)
            .Select(line => new PartnerLineCheckResult(line.LineId, PartnerLineCheckOutcome.NotChecked,
                Detail: $"Not asked live: at most {settings.MaxLiveChecks} Harbor checks fit Place order's time limit."));

        // One GET per asked line, in parallel: the shared rate limiter queues them if the sync is busy at the same moment.
        var answers = await Task.WhenAll(asked.Select(line => CheckLineAsync(client, context, line, cancellationToken)));
        return [.. answers, .. notAsked];
    }

    /// <summary>Lines with a known quantity first, the smallest margin over what is asked for first; then the rest, largest quantity first.</summary>
    private static (int, long) TightnessRank(PartnerLineCheck line) =>
        line.KnownQuantity is { } known ? (0, (long)known - line.Quantity) : (1, -line.Quantity);

    private async Task<PartnerLineCheckResult> CheckLineAsync(HttpClient client, PartnerMappingContext context, PartnerLineCheck line, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "v2/catalog/items/" + Uri.EscapeDataString(line.SourceProductId));
            using var response = await client.SendToPartnerAsync(request, HttpCompletionOption.ResponseContentRead, "Harbor", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new PartnerLineCheckResult(line.LineId, PartnerLineCheckOutcome.Unavailable, AvailableQuantity: 0,
                    Detail: $"Harbor has no item {line.SourceProductId} (404).");
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                var error = await HarborErrors.ToExceptionAsync(response, $"GET {PartnerHttpClients.Describe(request.RequestUri)}", cancellationToken);
                return Unreachable(line, error.Message);
            }

            HarborItemDto? dto;
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                dto = await JsonSerializer.DeserializeAsync<HarborItemDto>(stream, HarborJson.Options, cancellationToken);
            }

            if (dto is null || !string.Equals(dto.ItemCode?.Trim(), line.SourceProductId, StringComparison.Ordinal))
            {
                return Unreachable(line, $"Harbor answered for item '{dto?.ItemCode}' instead of {line.SourceProductId}.");
            }

            var snapshot = HarborItemMapper.Map(dto, context, out var problem);
            return snapshot is null
                ? new PartnerLineCheckResult(line.LineId, PartnerLineCheckOutcome.Unavailable, Detail: problem)
                : Evaluate(line, snapshot);
        }
        catch (PartnerException ex)
        {
            return Unreachable(line, ex.Message);
        }
        catch (JsonException ex)
        {
            return Unreachable(line, $"Harbor item {line.SourceProductId} is not valid JSON: {ex.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Unreachable(line, $"Harbor did not answer for {line.SourceProductId} within the checkout time limit.");
        }
    }

    private PartnerLineCheckResult Evaluate(PartnerLineCheck line, PartnerProductSnapshot item)
    {
        var price = currency.ToUsd(item.Price, item.Currency).Usd;
        if (!item.IsActive || item.Category is null || price is null)
        {
            var why = !item.IsActive ? "discontinued" : item.Category is null ? $"in unmapped dept '{item.PartnerCategory}'" : "without a valid price";
            return new PartnerLineCheckResult(line.LineId, PartnerLineCheckOutcome.Unavailable, price, Detail: $"Harbor item {line.SourceProductId} is {why}.");
        }

        var stock = item.Stock ?? StockLevel.Unknown;
        if (stock.Quantity is { } onHand)
        {
            if (onHand <= 0)
            {
                return new PartnerLineCheckResult(line.LineId, PartnerLineCheckOutcome.Unavailable, price, 0, $"Harbor item {line.SourceProductId} is sold out.");
            }

            if (onHand < line.Quantity)
            {
                return new PartnerLineCheckResult(line.LineId, PartnerLineCheckOutcome.Short, price, onHand,
                    $"Harbor has {onHand} of {line.SourceProductId}; {line.Quantity} asked for.");
            }
        }
        else if (stock.StatusWhenQuantityUnknown == AvailabilityStatus.OutOfStock)
        {
            // An inventory status Lumen does not know: never sell without evidence of stock (BR-CAT-01).
            return new PartnerLineCheckResult(line.LineId, PartnerLineCheckOutcome.Unavailable, price, Detail: $"Harbor item {line.SourceProductId} has no usable stock status.");
        }

        if (price != line.ExpectedUnitPriceUsd)
        {
            logger.LogInformation("Harbor live price of {Item} is {Live} USD; the customer reviewed {Expected} USD", line.SourceProductId, price, line.ExpectedUnitPriceUsd);
            return new PartnerLineCheckResult(line.LineId, PartnerLineCheckOutcome.PriceChanged, price, stock.Quantity);
        }

        return new PartnerLineCheckResult(line.LineId, PartnerLineCheckOutcome.Ok, price, stock.Quantity);
    }

    private PartnerLineCheckResult Unreachable(PartnerLineCheck line, string detail)
    {
        logger.LogWarning("Harbor live check for {Item} failed: {Detail}", line.SourceProductId, detail);
        return new PartnerLineCheckResult(line.LineId, PartnerLineCheckOutcome.Unreachable, Detail: detail);
    }
}
