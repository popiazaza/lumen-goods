using System.ComponentModel.DataAnnotations;
using Lumen.Domain.Payments;

namespace Lumen.UnitTests.Payments;

public sealed class PaymentWindowPolicyTests
{
    private static readonly DateTimeOffset PlacedAt = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WithinHold = PlacedAt.AddMinutes(5);

    private readonly PaymentWindowPolicy _policy = new(new PaymentOptions());

    [Theory]
    [Trait("BR", "BR-PAY-03")]
    [InlineData(0, 5, false)]
    [InlineData(4, 1, false)]
    [InlineData(5, 0, true)] // the fifth failure expires the order
    [InlineData(6, 0, true)]
    public void BR_PAY_03_an_order_allows_five_payment_attempts(int failedAttempts, int attemptsLeft, bool mustExpire)
    {
        _policy.AttemptsLeft(failedAttempts).Should().Be(attemptsLeft);
        _policy.CanStartAttempt(failedAttempts, PlacedAt, WithinHold).Should().Be(!mustExpire);
        _policy.MustExpireOrder(failedAttempts, PlacedAt, WithinHold, paymentInProgress: false).Should().Be(mustExpire);
    }

    [Fact]
    [Trait("BR", "BR-PAY-03")]
    public void BR_PAY_03_the_attempt_limit_is_configurable()
    {
        var policy = new PaymentWindowPolicy(new PaymentOptions { MaxAttemptsPerOrder = 3 });

        policy.MaxAttempts.Should().Be(3);
        policy.MustExpireOrder(3, PlacedAt, WithinHold, paymentInProgress: false).Should().BeTrue();
    }

    [Fact]
    [Trait("BR", "BR-STOCK-01")]
    public void BR_STOCK_01_the_hold_ends_15_minutes_after_the_order_is_placed()
    {
        // Scenario 2: 2 units reserved and no payment; 15 minutes pass.
        _policy.HoldEndsAt(PlacedAt).Should().Be(PlacedAt.AddMinutes(15));
        _policy.MustExpireOrder(0, PlacedAt, PlacedAt.AddMinutes(15).AddSeconds(-1), paymentInProgress: false).Should().BeFalse();
        _policy.MustExpireOrder(0, PlacedAt, PlacedAt.AddMinutes(15), paymentInProgress: false).Should().BeTrue();
    }

    [Fact]
    [Trait("BR", "BR-STOCK-01")]
    public void BR_STOCK_01_an_order_with_a_payment_in_progress_does_not_expire()
    {
        // Scenario 3: a card 0042 payment is still running when the hold ends; it must be allowed to finish.
        _policy.MustExpireOrder(0, PlacedAt, PlacedAt.AddMinutes(16), paymentInProgress: true).Should().BeFalse();
        _policy.MustExpireOrder(5, PlacedAt, WithinHold, paymentInProgress: true).Should().BeFalse();
    }

    [Fact]
    [Trait("BR", "BR-STOCK-01")]
    public void BR_STOCK_01_no_new_attempt_starts_once_the_hold_is_over() =>
        _policy.CanStartAttempt(0, PlacedAt, PlacedAt.AddMinutes(15)).Should().BeFalse();

    [Fact]
    public void Invalid_options_are_rejected_when_the_policy_is_built() =>
        FluentActions.Invoking(() => new PaymentWindowPolicy(new PaymentOptions { MaxAttemptsPerOrder = 0 }))
            .Should().Throw<ValidationException>();
}
