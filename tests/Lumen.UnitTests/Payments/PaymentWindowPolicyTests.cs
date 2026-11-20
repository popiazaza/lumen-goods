using System.ComponentModel.DataAnnotations;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;

namespace Lumen.UnitTests.Payments;

public sealed class PaymentWindowPolicyTests
{
    private static readonly DateTimeOffset PlacedAt = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WithinHold = PlacedAt.AddMinutes(5);
    private static readonly DateTimeOffset HoldOver = PlacedAt.AddMinutes(15);

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
        _policy.MustExpireOrder(failedAttempts, PlacedAt, WithinHold, currentPayment: null).Should().Be(mustExpire);
    }

    [Fact]
    [Trait("BR", "BR-PAY-03")]
    public void BR_PAY_03_the_attempt_limit_is_configurable()
    {
        var policy = new PaymentWindowPolicy(new PaymentOptions { MaxAttemptsPerOrder = 3 });

        policy.MaxAttempts.Should().Be(3);
        policy.MustExpireOrder(3, PlacedAt, WithinHold, currentPayment: null).Should().BeTrue();
    }

    [Fact]
    [Trait("BR", "BR-STOCK-01")]
    public void BR_STOCK_01_the_hold_ends_15_minutes_after_the_order_is_placed()
    {
        // Scenario 2: 2 units reserved and no payment; 15 minutes pass.
        _policy.HoldEndsAt(PlacedAt).Should().Be(HoldOver);
        _policy.MustExpireOrder(0, PlacedAt, HoldOver.AddSeconds(-1), currentPayment: null).Should().BeFalse();
        _policy.MustExpireOrder(0, PlacedAt, HoldOver, currentPayment: null).Should().BeTrue();
    }

    [Fact]
    [Trait("BR", "BR-STOCK-01")]
    public void BR_STOCK_01_a_slow_card_payment_still_running_at_the_end_of_the_hold_keeps_the_order_and_then_pays_it()
    {
        // Scenario 3: a card 0042 payment is still running when the hold ends; it must be allowed to finish.
        var running = new PaymentSnapshot(PaymentStatus.Processing, HoldOver.AddSeconds(-5));

        _policy.MustExpireOrder(0, PlacedAt, HoldOver.AddMinutes(1), running).Should().BeFalse();
        _policy.NextOrderStatusAfter(PaymentStatus.Succeeded, 0, PlacedAt, HoldOver.AddMinutes(1)).Should().Be(OrderStatus.Paid);
    }

    [Fact]
    [Trait("BR", "BR-STOCK-01")]
    public void BR_STOCK_01_no_new_attempt_starts_once_the_hold_is_over() =>
        _policy.CanStartAttempt(0, PlacedAt, HoldOver).Should().BeFalse();

    [Theory]
    [Trait("BR", "BR-STOCK-01")]
    [InlineData(PaymentStatus.Created, true)]
    [InlineData(PaymentStatus.Processing, true)]
    [InlineData(PaymentStatus.Succeeded, false)]
    [InlineData(PaymentStatus.Declined, false)]
    [InlineData(PaymentStatus.Failed, false)]
    [InlineData(PaymentStatus.Expired, false)]
    public void BR_STOCK_01_created_and_processing_payments_are_always_in_progress_and_outcomes_never(PaymentStatus status, bool inProgress) =>
        _policy.IsPaymentInProgress(new PaymentSnapshot(status, PlacedAt), PlacedAt.AddDays(1)).Should().Be(inProgress);

    [Fact]
    [Trait("BR", "BR-PAY-03")]
    public void BR_PAY_03_a_payment_waiting_for_a_code_is_in_progress_only_until_the_code_expires()
    {
        var waiting = new PaymentSnapshot(PaymentStatus.RequiresAction, PlacedAt.AddMinutes(3));

        _policy.IsPaymentInProgress(waiting, PlacedAt.AddMinutes(8).AddSeconds(-1)).Should().BeTrue();
        _policy.IsPaymentInProgress(waiting, PlacedAt.AddMinutes(8)).Should().BeFalse();
        _policy.ClosingOutcome(waiting, PlacedAt.AddMinutes(7)).Should().BeNull();
        _policy.ClosingOutcome(waiting, PlacedAt.AddMinutes(8)).Should().Be(PaymentStatus.Expired);
    }

    [Fact]
    [Trait("BR", "BR-PAY-03")]
    [Trait("BR", "BR-STOCK-01")]
    public void BR_STOCK_01_an_abandoned_one_time_code_releases_the_order_at_the_end_of_the_hold()
    {
        // Code issued at 10:03 and never entered: it expires at 10:08; the order can still be paid until 10:15.
        var abandoned = new PaymentSnapshot(PaymentStatus.RequiresAction, PlacedAt.AddMinutes(3));
        var codeExpired = PlacedAt.AddMinutes(8);

        var outcome = _policy.ClosingOutcome(abandoned, codeExpired);
        outcome.Should().Be(PaymentStatus.Expired);
        PaymentTransitions.Payment.Move(abandoned.Status, outcome!.Value).Should().Be(PaymentStatus.Expired);
        _policy.NextOrderStatusAfter(outcome.Value, 0, PlacedAt, codeExpired).Should().Be(OrderStatus.AwaitingPayment);

        var closed = abandoned with { Status = PaymentStatus.Expired, StatusSince = codeExpired };
        _policy.MustExpireOrder(1, PlacedAt, HoldOver.AddSeconds(-1), closed).Should().BeFalse();
        _policy.MustExpireOrder(1, PlacedAt, HoldOver, closed).Should().BeTrue();
    }

    [Fact]
    [Trait("BR", "BR-PAY-03")]
    [Trait("BR", "BR-STOCK-01")]
    public void BR_STOCK_01_a_code_issued_just_before_the_hold_ends_keeps_the_order_until_the_code_expires()
    {
        // Code issued at 10:12: the customer may still enter it until 10:17, after the 10:15 hold.
        var waiting = new PaymentSnapshot(PaymentStatus.RequiresAction, PlacedAt.AddMinutes(12));

        _policy.MustExpireOrder(0, PlacedAt, HoldOver, waiting).Should().BeFalse();

        var codeExpired = PlacedAt.AddMinutes(17);
        _policy.ClosingOutcome(waiting, codeExpired).Should().Be(PaymentStatus.Expired);
        _policy.NextOrderStatusAfter(PaymentStatus.Expired, 0, PlacedAt, codeExpired).Should().Be(OrderStatus.Expired);
    }

    [Fact]
    [Trait("BR", "BR-PAY-05")]
    public void BR_PAY_05_the_first_gateway_error_is_retried_once_automatically()
    {
        var firstError = new PaymentSnapshot(PaymentStatus.Error, WithinHold);

        _policy.IsPaymentInProgress(firstError, WithinHold).Should().BeTrue();
        _policy.ShouldRetryAutomatically(firstError, WithinHold).Should().BeTrue();
        _policy.ClosingOutcome(firstError, WithinHold).Should().BeNull();
    }

    [Fact]
    [Trait("BR", "BR-PAY-05")]
    [Trait("BR", "BR-STOCK-01")]
    public void BR_STOCK_01_a_payment_that_errors_twice_fails_and_the_order_is_released_at_the_end_of_the_hold()
    {
        // The automatic retry errored too: no second automatic retry, the payment is Failed and the order can be paid again.
        var secondError = new PaymentSnapshot(PaymentStatus.Error, WithinHold, AutomaticRetryUsed: true);

        _policy.ShouldRetryAutomatically(secondError, WithinHold).Should().BeFalse();
        _policy.IsPaymentInProgress(secondError, WithinHold).Should().BeFalse();
        _policy.ClosingOutcome(secondError, WithinHold).Should().Be(PaymentStatus.Failed);
        _policy.NextOrderStatusAfter(PaymentStatus.Failed, 0, PlacedAt, WithinHold).Should().Be(OrderStatus.AwaitingPayment);

        var failed = secondError with { Status = PaymentStatus.Failed };
        _policy.MustExpireOrder(1, PlacedAt, HoldOver.AddSeconds(-1), failed).Should().BeFalse();
        _policy.MustExpireOrder(1, PlacedAt, HoldOver, failed).Should().BeTrue();
    }

    [Fact]
    [Trait("BR", "BR-PAY-05")]
    [Trait("BR", "BR-STOCK-01")]
    public void BR_STOCK_01_a_retry_that_never_ran_stops_holding_the_order_after_the_retry_window()
    {
        // E.g. the process stopped between the error and the retry.
        var stuck = new PaymentSnapshot(PaymentStatus.Error, WithinHold);

        _policy.IsPaymentInProgress(stuck, WithinHold.AddSeconds(29)).Should().BeTrue();
        _policy.IsPaymentInProgress(stuck, WithinHold.AddSeconds(30)).Should().BeFalse();
        _policy.ShouldRetryAutomatically(stuck, WithinHold.AddSeconds(30)).Should().BeFalse();
        _policy.ClosingOutcome(stuck, WithinHold.AddSeconds(30)).Should().Be(PaymentStatus.Failed);
    }

    [Theory]
    [Trait("BR", "BR-PAY-03")]
    [InlineData(PaymentStatus.Succeeded, 0, 5, OrderStatus.Paid)]
    [InlineData(PaymentStatus.Succeeded, 4, 20, OrderStatus.Paid)]          // paid even after the hold
    [InlineData(PaymentStatus.Declined, 0, 5, OrderStatus.AwaitingPayment)]
    [InlineData(PaymentStatus.Failed, 3, 5, OrderStatus.AwaitingPayment)]
    [InlineData(PaymentStatus.Expired, 0, 5, OrderStatus.AwaitingPayment)]
    [InlineData(PaymentStatus.Declined, 4, 5, OrderStatus.Expired)]         // fifth failure
    [InlineData(PaymentStatus.Failed, 4, 5, OrderStatus.Expired)]
    [InlineData(PaymentStatus.Expired, 4, 5, OrderStatus.Expired)]
    [InlineData(PaymentStatus.Declined, 0, 15, OrderStatus.Expired)]        // hold over
    public void BR_PAY_03_each_payment_outcome_decides_the_next_order_status(
        PaymentStatus outcome, int failedAttemptsBefore, int minutesAfterPlacing, OrderStatus expected)
    {
        var next = _policy.NextOrderStatusAfter(outcome, failedAttemptsBefore, PlacedAt, PlacedAt.AddMinutes(minutesAfterPlacing));

        next.Should().Be(expected);
        OrderTransitions.Order.CanMove(OrderStatus.PaymentProcessing, next).Should().BeTrue();
    }

    [Theory]
    [InlineData(PaymentStatus.Created)]
    [InlineData(PaymentStatus.Processing)]
    [InlineData(PaymentStatus.RequiresAction)]
    [InlineData(PaymentStatus.Error)]
    public void A_payment_still_running_has_no_order_outcome_yet(PaymentStatus status) =>
        FluentActions.Invoking(() => _policy.NextOrderStatusAfter(status, 0, PlacedAt, WithinHold))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Invalid_options_are_rejected_when_the_policy_is_built() =>
        FluentActions.Invoking(() => new PaymentWindowPolicy(new PaymentOptions { MaxAttemptsPerOrder = 0 }))
            .Should().Throw<ValidationException>();
}
