using Lumen.Api.Partners.Harbor;
using Lumen.Api.Partners.Kestrel;
using Lumen.Api.Partners.Ports;
using Lumen.Domain.Pricing;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Checkout;

/// <summary>One partner line to check live: which partner, and what the customer is about to buy.</summary>
public sealed record PartnerLineToCheck(string Partner, PartnerLineCheck Line);

/// <summary>
/// BR-CHK-02: runs every partner's live check for Place order, all partners at once, inside one time budget
/// (<see cref="CheckoutOptions.PartnerCheckTimeout"/>, 5 s). A partner that is slow, down, throws, or has no check
/// registered makes its lines Unreachable ("Temporarily unavailable"): the order is never created on a guess. Runs
/// before the order's database transaction, never inside it (no partner I/O in a transaction).
/// </summary>
internal sealed class PartnerAvailability(
    IEnumerable<IPartnerAvailabilityCheck> checks,
    IOptionsMonitor<CheckoutOptions> options,
    TimeProvider time,
    ILogger<PartnerAvailability> logger)
{
    /// <returns>One result per line, keyed by <see cref="PartnerLineCheck.LineId"/>.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled (the client went away).</exception>
    public async Task<IReadOnlyDictionary<Guid, PartnerLineCheckResult>> CheckAsync(
        IReadOnlyList<PartnerLineToCheck> lines, CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
        {
            return new Dictionary<Guid, PartnerLineCheckResult>();
        }

        var budget = options.CurrentValue.PartnerCheckTimeout;

        // The timer starts here, before any partner is called, so the budget covers the whole check.
        using var timeout = new CancellationTokenSource(budget, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var perPartner = await Task.WhenAll(lines
            .GroupBy(l => l.Partner, StringComparer.Ordinal)
            .Select(group => CheckPartnerAsync(group.Key, [.. group.Select(l => l.Line)], budget, linked.Token)));
        cancellationToken.ThrowIfCancellationRequested();

        var results = perPartner.SelectMany(r => r).Select(WithinPriceCap).ToDictionary(r => r.LineId);
        foreach (var (_, line) in lines)
        {
            // An adapter that left a line out did not check it: not sold on a guess.
            results.TryAdd(line.LineId, new PartnerLineCheckResult(line.LineId, PartnerLineCheckOutcome.Unreachable, Detail: "No answer for this line."));
        }

        return results;
    }

    /// <summary>
    /// A live USD price above <see cref="PriceLimits.MaxUnitPriceUsd"/> is a data error, whichever adapter reported it
    /// (the partners' own converter already refuses one): the line is Unavailable, so Place order refuses it and its
    /// write-through makes the product not sellable, as for an invalid price. An order of such lines could not be saved.
    /// </summary>
    private PartnerLineCheckResult WithinPriceCap(PartnerLineCheckResult result)
    {
        if (result.CurrentUnitPriceUsd is not { } price || price <= PriceLimits.MaxUnitPriceUsd)
        {
            return result;
        }

        logger.LogWarning("Live check priced line {LineId} at {Price} USD, above the highest unit price Lumen sells; not sold", result.LineId, price);
        return new PartnerLineCheckResult(result.LineId, PartnerLineCheckOutcome.Unavailable,
            Detail: $"Live price {price} USD is above the highest unit price Lumen sells.");
    }

    private async Task<IReadOnlyList<PartnerLineCheckResult>> CheckPartnerAsync(
        string partner, IReadOnlyList<PartnerLineCheck> lines, TimeSpan budget, CancellationToken cancellationToken)
    {
        var check = checks.FirstOrDefault(c => string.Equals(c.Partner, partner, StringComparison.Ordinal));
        if (check is null)
        {
            return Unreachable(lines, $"No live availability check is registered for partner '{partner}'.");
        }

        try
        {
            // WaitAsync holds the budget even for an adapter that ignored the token.
            var results = await check.CheckAsync(lines, cancellationToken).WaitAsync(budget, time, CancellationToken.None);
            return [.. results.Where(r => lines.Any(l => l.LineId == r.LineId))];
        }
        catch (TimeoutException)
        {
            return Unreachable(lines, $"{partner} did not answer within {budget.TotalSeconds:0.#} s.");
        }
        catch (OperationCanceledException)
        {
            return Unreachable(lines, $"{partner} check was cancelled.");
        }
        catch (Exception ex)
        {
            // An adapter must not throw for a partner failure; if one does, the line is still not sold on a guess.
            logger.LogError(ex, "Live availability check for {Partner} threw", partner);
            return Unreachable(lines, $"{partner} check failed: {ex.Message}");
        }
    }

    private static PartnerLineCheckResult[] Unreachable(IReadOnlyList<PartnerLineCheck> lines, string detail) =>
        [.. lines.Select(l => new PartnerLineCheckResult(l.LineId, PartnerLineCheckOutcome.Unreachable, Detail: detail))];
}

internal static class PartnerAvailabilityRegistration
{
    /// <summary>
    /// BR-CHK-02: the live checks Place order runs, one per partner, on the partners' existing named clients (so Harbor's
    /// shared rate limit and both partners' credentials and resilience apply). Called from <c>AddCheckout</c>.
    /// </summary>
    public static IServiceCollection AddPartnerAvailabilityChecks(this IServiceCollection services)
    {
        services.AddSingleton<IPartnerAvailabilityCheck, HarborAvailabilityCheck>();
        services.AddSingleton<IPartnerAvailabilityCheck, KestrelAvailabilityCheck>();
        services.AddSingleton<PartnerAvailability>();
        return services;
    }
}
