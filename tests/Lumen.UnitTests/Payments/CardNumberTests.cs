using Lumen.Domain.Payments;

namespace Lumen.UnitTests.Payments;

/// <summary>Builds Luhn-valid numbers of any length, computed here independently of <see cref="CardNumber.PassesLuhn"/>.</summary>
internal static class Luhn
{
    public static string Complete(string prefix, int length = 16)
    {
        var body = prefix.PadRight(length - 1, '0');
        var sum = 0;
        for (var i = 0; i < body.Length; i++)
        {
            // Once the check digit is appended, the body's last digit is second from the right, so it is doubled.
            var digit = body[body.Length - 1 - i] - '0';
            if (i % 2 == 0)
            {
                digit = digit * 2 > 9 ? (digit * 2) - 9 : digit * 2;
            }

            sum += digit;
        }

        return body + ((10 - (sum % 10)) % 10);
    }
}

public sealed class CardNumberTests
{
    public static TheoryData<string> SpecTestCards() =>
    [
        "4111 1111 1111 1111",
        "4242 0000 0000 0018",
        "4242 0000 0000 0026",
        "4242 0000 0000 0034",
        "4242 0000 0000 0042",
        "4242 0000 0000 0059",
    ];

    [Theory]
    [MemberData(nameof(SpecTestCards))]
    [Trait("BR", "BR-PAY-02")]
    public void BR_PAY_02_every_spec_test_card_is_a_valid_luhn_number(string number)
    {
        CardNumber.TryParse(number, out var card).Should().BeTrue();

        card!.Digits.Should().Be(number.Replace(" ", "", StringComparison.Ordinal));
        card.Brand.Should().Be(CardBrand.Visa);
    }

    [Theory]
    [Trait("BR", "BR-PAY-02")]
    [InlineData("4111 1111 1111 1112")] // scenario 2
    [InlineData("4242 0000 0000 0019")]
    [InlineData("4111 1111 1111 1121")] // two digits swapped
    public void BR_PAY_02_a_number_failing_the_luhn_check_is_rejected(string number) =>
        CardNumber.TryParse(number, out _).Should().BeFalse();

    [Theory]
    [Trait("BR", "BR-PAY-02")]
    [InlineData("4111111111111111")]
    [InlineData("4111-1111-1111-1111")]
    [InlineData(" 4111 1111 1111 1111 ")]
    [InlineData("4111 - 1111 - 1111 - 1111")]
    public void BR_PAY_02_spaces_and_dashes_are_ignored(string number)
    {
        CardNumber.TryParse(number, out var card).Should().BeTrue();

        card!.Digits.Should().Be("4111111111111111");
    }

    [Theory]
    [Trait("BR", "BR-PAY-02")]
    [InlineData("4111.1111.1111.1111")]
    [InlineData("4111_1111_1111_1111")]
    [InlineData("+4111111111111111")]
    [InlineData("4111 1111 1111 111a")]
    [InlineData("４１１１１１１１１１１１１１１１")] // full-width digits are not ASCII digits
    public void BR_PAY_02_any_other_character_makes_the_number_invalid(string number) =>
        CardNumber.TryParse(number, out _).Should().BeFalse();

    [Theory]
    [Trait("BR", "BR-PAY-02")]
    [InlineData(11, false)]
    [InlineData(12, true)]
    [InlineData(13, true)]
    [InlineData(16, true)]
    [InlineData(19, true)]
    [InlineData(20, false)]
    public void BR_PAY_02_only_12_to_19_digits_are_accepted(int length, bool expected) =>
        CardNumber.TryParse(Luhn.Complete("4", length), out _).Should().Be(expected);

    [Theory]
    [Trait("BR", "BR-PAY-02")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("----")]
    public void BR_PAY_02_a_missing_number_is_rejected(string? number) =>
        CardNumber.TryParse(number, out _).Should().BeFalse();

    [Theory]
    [Trait("BR", "BR-PAY-02")]
    [InlineData("4", 16, CardBrand.Visa)]
    [InlineData("4", 13, CardBrand.Visa)]
    [InlineData("51", 16, CardBrand.Mastercard)]
    [InlineData("55", 16, CardBrand.Mastercard)]
    [InlineData("2221", 16, CardBrand.Mastercard)]
    [InlineData("2500", 16, CardBrand.Mastercard)]
    [InlineData("2720", 16, CardBrand.Mastercard)]
    [InlineData("34", 15, CardBrand.Amex)]
    [InlineData("37", 15, CardBrand.Amex)]
    [InlineData("50", 16, CardBrand.Other)]
    [InlineData("56", 16, CardBrand.Other)]
    [InlineData("2220", 16, CardBrand.Other)] // just below the Mastercard 2-series
    [InlineData("2721", 16, CardBrand.Other)] // just above it
    [InlineData("35", 16, CardBrand.Other)]
    [InlineData("6011", 16, CardBrand.Other)]
    public void BR_PAY_02_the_brand_comes_from_the_issuer_prefix(string prefix, int length, CardBrand expected)
    {
        CardNumber.TryParse(Luhn.Complete(prefix, length), out var card).Should().BeTrue();

        card!.Brand.Should().Be(expected);
    }

    [Theory]
    [Trait("BR", "BR-PAY-02")]
    [InlineData("5555 5555 5555 4444", CardBrand.Mastercard)]
    [InlineData("2223 0000 4841 0010", CardBrand.Mastercard)]
    [InlineData("3782 822463 10005", CardBrand.Amex)]
    [InlineData("6011 1111 1111 1117", CardBrand.Other)]
    public void BR_PAY_02_well_known_test_numbers_get_their_brand(string number, CardBrand expected)
    {
        CardNumber.TryParse(number, out var card).Should().BeTrue();

        card!.Brand.Should().Be(expected);
    }

    [Fact]
    [Trait("BR", "BR-PAY-02")]
    public void BR_PAY_02_only_the_brand_and_last_4_digits_appear_in_text()
    {
        CardNumber.TryParse("4111 1111 1111 1111", out var card).Should().BeTrue();

        card!.Last4.Should().Be("1111");
        card.ToString().Should().Be("Visa ending 1111");
        $"{card}".Should().NotContain("41111111");
    }
}
