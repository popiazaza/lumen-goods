namespace Lumen.Domain.Pricing;

/// <summary>Bounds on what Lumen sells at, so every order it can create fits its order tables.</summary>
public static class PriceLimits
{
    /// <summary>
    /// BR-ADM-01, BR-MONEY-01: the highest USD unit price Lumen sells at, for its own products (the admin form) and for
    /// partner products after conversion (sync and Place order's live check; a higher one is a data error and leaves the
    /// product not sellable). An order's money columns are numeric(12,2), so its total must stay at or below
    /// 9,999,999,999.99, or saving it fails. The largest order this allows, with every setting at the top of its
    /// validated range:
    /// <code>
    /// lines     50 lines (PlaceOrderRequest.MaxLines) x 10 units (CartLine.MaxQuantity) x 999,999.99 =   499,999,995.00
    /// tax       at most 100 % of the lines (Pricing:TaxRate is 0 to 1)                                 =   499,999,995.00
    /// shipping  at most 3 shipments, one per seller, x 1,000.00 (Pricing fees are 0 to 1000)           =         3,000.00
    /// total                                                                                             = 1,000,002,990.00
    /// </code>
    /// One more digit (9,999,999.99) would make that 10,000,002,990.00, which does not fit.
    /// </summary>
    public const decimal MaxUnitPriceUsd = 999_999.99m;
}
