namespace Lumen.Api.Partners.Ports;

/// <summary>
/// Port (BR-CHK-02): checks a partner's lines live just before an order is created, so a customer never pays for
/// something the partner cannot supply or at a price they have not seen. One adapter per partner:
/// Harbor reads each item (<c>GET /v2/catalog/items/{item_code}</c>: price, status, on hand); Kestrel reads quantities
/// from <c>/stock</c> (it has no price lookup, so its price stays the latest feed's and is not reported as changed).
/// The caller bounds the whole check with <c>Checkout:PartnerCheckTimeout</c>; an adapter never throws for a partner
/// that is slow or down, it answers <see cref="PartnerLineCheckOutcome.Unreachable"/> for the lines it could not check.
/// <para>
/// Write-through: what a check learns is saved before Place order answers. Place order writes each live price
/// (<see cref="PartnerLineCheckResult.CurrentUnitPriceUsd"/>) and availability (<see cref="PartnerLineCheckResult.AvailableQuantity"/>,
/// or out of stock / not sellable for <see cref="PartnerLineCheckOutcome.Unavailable"/>) to the product's row, then
/// prices the order, or the 409's <c>newTotal</c>, from those same rows. A customer who re-confirms the new total is
/// therefore checked against the values they were shown, and the cart and catalogue show them too, instead of the
/// next Place order finding the same difference again (until the partner's own sync would have caught up).
/// An adapter itself writes nothing: it only reports.
/// </para>
/// </summary>
public interface IPartnerAvailabilityCheck
{
    /// <summary>Partner code, equal to its product source (e.g. "harbor").</summary>
    string Partner { get; }

    /// <returns>One result per requested line, matched by <see cref="PartnerLineCheck.LineId"/>.</returns>
    Task<IReadOnlyList<PartnerLineCheckResult>> CheckAsync(IReadOnlyList<PartnerLineCheck> lines, CancellationToken cancellationToken);
}

/// <param name="LineId">The caller's id for the line (the cart line), echoed in the result.</param>
/// <param name="SourceProductId">Harbor item_code or Kestrel ref.</param>
/// <param name="ExpectedUnitPriceUsd">The USD unit price the customer reviewed; a different live price is <see cref="PartnerLineCheckOutcome.PriceChanged"/>.</param>
public sealed record PartnerLineCheck(Guid LineId, string SourceProductId, int Quantity, decimal ExpectedUnitPriceUsd);

public enum PartnerLineCheckOutcome
{
    /// <summary>Available in the quantity asked for, at the expected price.</summary>
    Ok,

    /// <summary>Available, but the live price differs (up or down): BR-CHK-01's 409 with the new price.</summary>
    PriceChanged,

    /// <summary>Fewer units than asked for: "Only {n} available".</summary>
    Short,

    /// <summary>Not sold any more (discontinued, inactive, unknown item, no price) or none in stock.</summary>
    Unavailable,

    /// <summary>The partner did not answer in time or answered with an error: "Temporarily unavailable".</summary>
    Unreachable,
}

/// <param name="CurrentUnitPriceUsd">The live price converted to USD (BR-MONEY-01); null when the partner gives no live price.</param>
/// <param name="AvailableQuantity">Short: how many can be ordered now; null when the partner gives no number.</param>
/// <param name="Detail">For logs and the admin's partner errors, e.g. "Harbor 429, Retry-After 12 s". Never shown to the customer.</param>
public sealed record PartnerLineCheckResult(
    Guid LineId,
    PartnerLineCheckOutcome Outcome,
    decimal? CurrentUnitPriceUsd = null,
    int? AvailableQuantity = null,
    string? Detail = null);
