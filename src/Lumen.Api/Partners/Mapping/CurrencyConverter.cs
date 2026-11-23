using System.Globalization;
using Lumen.Domain.Money;
using Lumen.Domain.Pricing;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Partners.Mapping;

/// <summary>A partner unit price expressed in USD, with what it was converted from.</summary>
/// <param name="Problem">Why there is no USD price although the partner sent one; null when fine.</param>
public sealed record UsdPrice(decimal? Usd, decimal? SourcePrice, string SourceCurrency, decimal? RateToUsd, string? Problem);

/// <summary>
/// BR-MONEY-01: partner prices become USD per unit with the configured rate (<see cref="FxOptions"/>), rounded half away
/// from zero to cents by <see cref="MoneyMath"/>. Unsupported currencies, non-positive prices and prices above
/// <see cref="PriceLimits.MaxUnitPriceUsd"/> once in USD give no price (not sellable), so no order can outgrow its totals.
/// </summary>
public sealed class CurrencyConverter(IOptionsMonitor<FxOptions> fx)
{
    public const string Usd = "USD";
    public const string Eur = "EUR";

    /// <summary>
    /// Largest unit price accepted in the partner's currency (it is stored as sent, in a numeric(12,2) column); anything
    /// above is treated as a data error rather than stored. The USD price must also stay within
    /// <see cref="PriceLimits.MaxUnitPriceUsd"/>.
    /// </summary>
    public const decimal MaxUnitPrice = 9_999_999.99m;

    public UsdPrice ToUsd(decimal? price, string? currency)
    {
        var code = (currency ?? "").Trim().ToUpperInvariant();
        if (code.Length != 3 || !code.All(char.IsAsciiLetterUpper))
        {
            return new UsdPrice(null, null, "XXX", null, price is null ? null : $"currency '{currency}' is not an ISO 4217 code");
        }

        if (price is not { } amount)
        {
            return new UsdPrice(null, null, code, null, null);
        }

        if (amount <= 0 || amount > MaxUnitPrice)
        {
            return new UsdPrice(null, null, code, null, $"price {amount} {code} is not a valid unit price");
        }

        var source = MoneyMath.RoundToCents(amount);
        if (code == Usd)
        {
            return source > PriceLimits.MaxUnitPriceUsd ? AboveCap(amount, code, source) : new UsdPrice(source, source, Usd, null, null);
        }

        if (RateToUsd(code) is not { } rate)
        {
            return new UsdPrice(null, source, code, null, $"no exchange rate configured for {code}");
        }

        var usd = MoneyMath.ConvertToUsd(source, rate);
        return usd > PriceLimits.MaxUnitPriceUsd ? AboveCap(amount, code, usd) : new UsdPrice(usd, source, code, rate, null);
    }

    /// <summary>A data error like an invalid price: an order of 50 lines of 10 at this price could not be saved.</summary>
    private static UsdPrice AboveCap(decimal amount, string code, decimal usd) => new(null, null, code, null, string.Create(
        CultureInfo.InvariantCulture, $"price {amount} {code} is {usd} USD, above the highest unit price Lumen sells ({PriceLimits.MaxUnitPriceUsd} USD)"));

    /// <summary>Configured USD per unit of <paramref name="currency"/>, rounded to the 8 decimals stored with each product.</summary>
    public decimal? RateToUsd(string currency) => currency switch
    {
        Usd => 1m,
        Eur => Math.Round(fx.CurrentValue.EurToUsd, 8, MidpointRounding.AwayFromZero),
        _ => null,
    };

    /// <summary>Stable text form of the rates, so a rate change is noticed and unchanged items are re-priced.</summary>
    public string Fingerprint() => $"EUR={RateToUsd(Eur)?.ToString(CultureInfo.InvariantCulture)}";
}
