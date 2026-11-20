namespace Lumen.Domain.Money;

/// <summary>
/// The one place money is rounded. All amounts are <see cref="decimal"/> USD.
/// </summary>
public static class MoneyMath
{
    /// <summary>Rounds to cents, half away from zero (never .NET's default banker's rounding).</summary>
    public static decimal RoundToCents(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// BR-MONEY-01: a partner's unit price in another currency is converted per unit with the configured rate,
    /// then rounded to cents. Line totals are unit price × quantity, so they need no further rounding.
    /// Example: 1.249,00 EUR × 1.0850 = 1355.165 → 1355.17 USD.
    /// </summary>
    public static decimal ConvertToUsd(decimal unitPrice, decimal rateToUsd)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(unitPrice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rateToUsd);
        return RoundToCents(unitPrice * rateToUsd);
    }
}
