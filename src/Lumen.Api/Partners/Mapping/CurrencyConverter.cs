using System.Globalization;
using Lumen.Domain.Money;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Partners.Mapping;

/// <summary>A partner unit price expressed in USD, with what it was converted from.</summary>
/// <param name="Problem">Why there is no USD price although the partner sent one; null when fine.</param>
public sealed record UsdPrice(decimal? Usd, decimal? SourcePrice, string SourceCurrency, decimal? RateToUsd, string? Problem);

/// <summary>
/// BR-MONEY-01: partner prices become USD per unit with the configured rate (<see cref="FxOptions"/>), rounded half away
/// from zero to cents by <see cref="MoneyMath"/>. Unsupported currencies and non-positive prices give no price (not sellable).
/// </summary>
public sealed class CurrencyConverter(IOptionsMonitor<FxOptions> fx)
{
    public const string Usd = "USD";
    public const string Eur = "EUR";

    /// <summary>Largest unit price accepted; anything above is treated as a data error rather than stored.</summary>
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
            return new UsdPrice(source, source, Usd, null, null);
        }

        if (RateToUsd(code) is not { } rate)
        {
            return new UsdPrice(null, source, code, null, $"no exchange rate configured for {code}");
        }

        return new UsdPrice(MoneyMath.ConvertToUsd(source, rate), source, code, rate, null);
    }

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
