using Lumen.Api.Partners.Ports;
using Lumen.Domain.Catalog;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Partners.Kestrel;

/// <summary>
/// BR-CHK-02: Kestrel's live check before an order is created. Kestrel has no price lookup, so only quantities are
/// read live, from <c>/stock</c> through the sync's own <see cref="KestrelStockSource"/> (batching, CSV parsing, Basic
/// auth, resilience); the price stays the latest feed's and is never reported as changed here.
/// - 0 units, or a ref the CSV leaves out → Unavailable (never in stock without evidence, BR-CAT-01).
/// - Fewer units than the line asks for → Short with the number.
/// - A failed request or batch → Unreachable for its lines.
/// </summary>
internal sealed class KestrelAvailabilityCheck(
    KestrelStockSource stock,
    IOptionsMonitor<KestrelOptions> options,
    ILogger<KestrelAvailabilityCheck> logger) : IPartnerAvailabilityCheck
{
    public string Partner => ProductSources.Kestrel;

    public async Task<IReadOnlyList<PartnerLineCheckResult>> CheckAsync(IReadOnlyList<PartnerLineCheck> lines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (!options.CurrentValue.IsConfigured)
        {
            return [.. lines.Select(l => Unreachable(l, "Kestrel is not configured."))];
        }

        StockFetchResult result;
        try
        {
            result = await stock.FetchStockAsync([.. lines.Select(l => l.SourceProductId)], cancellationToken);
        }
        catch (PartnerException ex)
        {
            return [.. lines.Select(l => Unreachable(l, ex.Message))];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return [.. lines.Select(l => Unreachable(l, "Kestrel /stock did not answer within the checkout time limit."))];
        }

        return [.. lines.Select(line => Evaluate(line, result))];
    }

    private PartnerLineCheckResult Evaluate(PartnerLineCheck line, StockFetchResult result)
    {
        if (!result.Levels.TryGetValue(line.SourceProductId, out var level))
        {
            // Its batch failed (the others answered).
            return Unreachable(line, result.Failures.FirstOrDefault() ?? $"Kestrel /stock gave no answer for {line.SourceProductId}.");
        }

        return level.Quantity switch
        {
            null => new PartnerLineCheckResult(line.LineId, PartnerLineCheckOutcome.Unavailable,
                Detail: $"Kestrel /stock does not list {line.SourceProductId}."),
            <= 0 => new PartnerLineCheckResult(line.LineId, PartnerLineCheckOutcome.Unavailable, AvailableQuantity: 0,
                Detail: $"Kestrel has no units of {line.SourceProductId}."),
            { } units when units < line.Quantity => new PartnerLineCheckResult(line.LineId, PartnerLineCheckOutcome.Short, AvailableQuantity: units,
                Detail: $"Kestrel has {units} of {line.SourceProductId}; {line.Quantity} asked for."),
            { } units => new PartnerLineCheckResult(line.LineId, PartnerLineCheckOutcome.Ok, AvailableQuantity: units),
        };
    }

    private PartnerLineCheckResult Unreachable(PartnerLineCheck line, string detail)
    {
        logger.LogWarning("Kestrel live check for {Ref} failed: {Detail}", line.SourceProductId, detail);
        return new PartnerLineCheckResult(line.LineId, PartnerLineCheckOutcome.Unreachable, Detail: detail);
    }
}
