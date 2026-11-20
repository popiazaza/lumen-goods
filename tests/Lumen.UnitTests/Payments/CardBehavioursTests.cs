using Lumen.Domain.Payments;

namespace Lumen.UnitTests.Payments;

public sealed class CardBehavioursTests
{
    [Theory]
    [Trait("BR", "BR-PAY-02")]
    [InlineData("4111 1111 1111 1111", CardBehaviour.Approve)]
    [InlineData("4242 0000 0000 0018", CardBehaviour.DeclineInsufficientFunds)]
    [InlineData("4242 0000 0000 0026", CardBehaviour.DeclineStolen)]
    [InlineData("4242 0000 0000 0034", CardBehaviour.RequireOneTimeCode)]
    [InlineData("4242 0000 0000 0042", CardBehaviour.ApproveAfterDelay)]
    [InlineData("4242 0000 0000 0059", CardBehaviour.ErrorThenApprove)]
    public void BR_PAY_02_each_spec_test_card_gives_its_result(string number, CardBehaviour expected)
    {
        // Scenario 1 (outline): each test card → its result.
        CardNumber.TryParse(number, out var card).Should().BeTrue();

        CardBehaviours.For(card!).Should().Be(expected);
    }

    [Theory]
    [Trait("BR", "BR-PAY-02")]
    [InlineData("4242 4242 4242 4242")]
    [InlineData("5555 5555 5555 4444")]
    [InlineData("3782 822463 10005")]
    [InlineData("4242-0000-0000-0067")] // close to the special numbers, but not one of them
    public void BR_PAY_02_any_other_luhn_valid_number_is_approved(string number)
    {
        CardNumber.TryParse(number, out var card).Should().BeTrue();

        CardBehaviours.For(card!).Should().Be(CardBehaviour.Approve);
    }

    [Fact]
    [Trait("BR", "BR-PAY-02")]
    public void BR_PAY_02_the_table_holds_exactly_the_six_spec_numbers_and_each_is_a_valid_card()
    {
        CardBehaviours.TestCards.Should().HaveCount(6);
        CardBehaviours.TestCards.Keys.Should().AllSatisfy(n => CardNumber.TryParse(n, out _).Should().BeTrue());
    }

    [Fact]
    [Trait("BR", "BR-PAY-07")]
    public void BR_PAY_07_a_stolen_card_shows_the_customer_only_a_generic_decline()
    {
        // Scenario 1.
        DeclineMessages.ForCustomer(DeclineReason.CardReportedStolen).Should().Be("Your card was declined");
        DeclineMessages.ForCustomer(DeclineReason.CardReportedStolen).Should().NotContainEquivalentOf("stolen");
        DeclineMessages.ForAdmin(DeclineReason.CardReportedStolen).Should().Be("Card reported stolen");
    }

    [Fact]
    [Trait("BR", "BR-PAY-07")]
    public void BR_PAY_07_insufficient_funds_is_named_so_the_customer_can_pay_another_way() =>
        // Scenario 2.
        DeclineMessages.ForCustomer(DeclineReason.InsufficientFunds).Should().Be("Insufficient funds");

    [Fact]
    [Trait("BR", "BR-PAY-07")]
    public void BR_PAY_07_every_decline_reason_has_a_customer_and_an_admin_message() =>
        Enum.GetValues<DeclineReason>().Should().AllSatisfy(reason =>
        {
            DeclineMessages.Table.Should().ContainKey(reason);
            DeclineMessages.Table[reason].Customer.Should().NotBeNullOrWhiteSpace();
            DeclineMessages.Table[reason].Admin.Should().NotBeNullOrWhiteSpace();
        });
}
