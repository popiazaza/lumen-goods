using Lumen.Domain.Money;

namespace Lumen.UnitTests.Money;

public sealed class MoneyMathTests
{
    [Theory]
    [Trait("BR", "BR-MONEY-01")]
    [InlineData("1249.00", "1.0850", "1355.17")] // 1355.165: half rounds away from zero (banker's rounding would give .16)
    [InlineData("14.95", "1.0850", "16.22")]     // 16.22075
    [InlineData("0.01", "1.0850", "0.01")]
    [InlineData("10.00", "1.0000", "10.00")]
    public void BR_MONEY_01_converts_the_unit_price_and_rounds_half_away_from_zero(string unitPrice, string rate, string expected) =>
        MoneyMath.ConvertToUsd(decimal.Parse(unitPrice, System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [Trait("BR", "BR-MONEY-01")]
    [InlineData("2.345", "2.35")]
    [InlineData("2.355", "2.36")]
    [InlineData("-2.345", "-2.35")]
    public void BR_MONEY_01_rounding_never_uses_bankers_rounding(string amount, string expected) =>
        MoneyMath.RoundToCents(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Conversion_rejects_a_non_positive_rate() =>
        FluentActions.Invoking(() => MoneyMath.ConvertToUsd(10m, 0m)).Should().Throw<ArgumentOutOfRangeException>();
}
