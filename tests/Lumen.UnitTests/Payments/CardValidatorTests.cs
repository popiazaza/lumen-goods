using Lumen.Domain.Payments;

namespace Lumen.UnitTests.Payments;

public sealed class CardValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

    private static CardDetails Valid => new("4111 1111 1111 1111", 12, 2028, "123", "Ada Lovelace");

    [Fact]
    [Trait("BR", "BR-PAY-02")]
    public void BR_PAY_02_a_valid_card_passes_with_its_number_parsed()
    {
        var result = CardValidator.Validate(Valid, Now);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        result.Card!.Last4.Should().Be("1111");
    }

    [Fact]
    [Trait("BR", "BR-PAY-02")]
    public void BR_PAY_02_a_number_failing_luhn_is_a_field_error_and_no_card_is_returned()
    {
        // Scenario 2: the gateway is never called, because there is no card to send.
        var result = CardValidator.Validate(Valid with { Number = "4111 1111 1111 1112" }, Now);

        result.IsValid.Should().BeFalse();
        result.Card.Should().BeNull();
        result.Errors.Should().ContainKey("number").WhoseValue.Should().Be("Enter a valid card number.");
    }

    [Theory]
    [Trait("BR", "BR-PAY-02")]
    [InlineData(10, 2026)]
    [InlineData(12, 2025)]
    [InlineData(1, 2000)]
    [InlineData(10, 26)]
    public void BR_PAY_02_an_expiry_in_the_past_is_rejected(int month, int year) =>
        CardValidator.Validate(Valid with { ExpiryMonth = month, ExpiryYear = year }, Now)
            .Errors.Should().ContainKey("expiry").WhoseValue.Should().Be("This card has expired.");

    [Theory]
    [Trait("BR", "BR-PAY-02")]
    [InlineData(11, 2026)] // the current month is still valid
    [InlineData(11, 26)]
    [InlineData(12, 2026)]
    [InlineData(1, 27)]
    public void BR_PAY_02_an_expiry_this_month_or_later_is_accepted(int month, int year) =>
        CardValidator.Validate(Valid with { ExpiryMonth = month, ExpiryYear = year }, Now).IsValid.Should().BeTrue();

    [Fact]
    [Trait("BR", "BR-PAY-02")]
    public void BR_PAY_02_a_card_works_until_the_end_of_its_expiry_month()
    {
        CardValidator.IsExpired(11, 2026, new DateTimeOffset(2026, 11, 30, 23, 59, 59, TimeSpan.Zero)).Should().BeFalse();
        CardValidator.IsExpired(11, 2026, new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero)).Should().BeTrue();
    }

    [Theory]
    [Trait("BR", "BR-PAY-02")]
    [InlineData(0)]
    [InlineData(13)]
    public void BR_PAY_02_a_month_outside_1_to_12_is_rejected(int month) =>
        CardValidator.Validate(Valid with { ExpiryMonth = month }, Now)
            .Errors.Should().ContainKey("expiry").WhoseValue.Should().Be("Enter a valid expiry date.");

    [Theory]
    [Trait("BR", "BR-PAY-02")]
    [InlineData("4111 1111 1111 1111", "123", true)]
    [InlineData("4111 1111 1111 1111", "1234", false)]
    [InlineData("4111 1111 1111 1111", "12", false)]
    [InlineData("4111 1111 1111 1111", "12a", false)]
    [InlineData("4111 1111 1111 1111", "", false)]
    [InlineData("4111 1111 1111 1111", null, false)]
    [InlineData("5555 5555 5555 4444", "123", true)]
    [InlineData("3782 822463 10005", "1234", true)]
    [InlineData("3782 822463 10005", "123", false)]
    public void BR_PAY_02_the_cvc_has_3_digits_or_4_for_amex(string number, string? cvc, bool valid) =>
        CardValidator.Validate(Valid with { Number = number, Cvc = cvc }, Now).IsValid.Should().Be(valid);

    [Fact]
    [Trait("BR", "BR-PAY-02")]
    public void BR_PAY_02_the_cvc_message_names_the_length_the_brand_needs()
    {
        CardValidator.Validate(Valid with { Number = "3782 822463 10005", Cvc = "123" }, Now)
            .Errors["cvc"].Should().Be("Enter the 4-digit security code.");
        CardValidator.Validate(Valid with { Cvc = "1234" }, Now)
            .Errors["cvc"].Should().Be("Enter the 3-digit security code.");
    }

    [Theory]
    [Trait("BR", "BR-PAY-02")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BR_PAY_02_the_name_on_the_card_is_required(string? name) =>
        CardValidator.Validate(Valid with { HolderName = name }, Now)
            .Errors.Should().ContainKey("holderName");

    [Fact]
    [Trait("BR", "BR-PAY-02")]
    public void BR_PAY_02_every_invalid_field_is_reported_at_once() =>
        CardValidator.Validate(new CardDetails("1234", 10, 2026, "1", " "), Now)
            .Errors.Keys.Should().BeEquivalentTo(["number", "expiry", "cvc", "holderName"]);
}
