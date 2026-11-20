using Lumen.Domain.Payments;

namespace Lumen.UnitTests.Payments;

public sealed class PaymentTransitionTests
{
    public static TheoryData<PaymentStatus, PaymentStatus, bool> PaymentMoves() => TransitionCases.AllPairs(
        (PaymentStatus.Created, PaymentStatus.Processing),
        (PaymentStatus.Processing, PaymentStatus.Succeeded),
        (PaymentStatus.Processing, PaymentStatus.Declined),
        (PaymentStatus.Processing, PaymentStatus.RequiresAction),
        (PaymentStatus.Processing, PaymentStatus.Error),
        (PaymentStatus.RequiresAction, PaymentStatus.Succeeded),
        (PaymentStatus.RequiresAction, PaymentStatus.Declined),
        (PaymentStatus.RequiresAction, PaymentStatus.Expired),
        (PaymentStatus.Error, PaymentStatus.Processing),
        (PaymentStatus.Error, PaymentStatus.Failed),
        (PaymentStatus.Failed, PaymentStatus.Processing),
        (PaymentStatus.Failed, PaymentStatus.Succeeded),
        (PaymentStatus.Failed, PaymentStatus.Declined));

    [Theory]
    [MemberData(nameof(PaymentMoves))]
    [Trait("BR", "BR-PAY-01")]
    public void BR_PAY_01_payment_status_moves_only_along_its_table(PaymentStatus from, PaymentStatus to, bool legal) =>
        TransitionCases.AssertMove(PaymentTransitions.Payment, from, to, legal);

    [Theory]
    [Trait("BR", "BR-PAY-01")]
    [InlineData(PaymentStatus.Succeeded)]
    [InlineData(PaymentStatus.Declined)]
    [InlineData(PaymentStatus.Expired)]
    public void BR_PAY_01_a_finished_payment_never_runs_again(PaymentStatus status) =>
        PaymentTransitions.Payment.IsFinal(status).Should().BeTrue();

    [Fact]
    [Trait("BR", "BR-PAY-03")]
    public void BR_PAY_03_a_payment_waiting_for_a_code_ends_approved_declined_or_expired() =>
        PaymentTransitions.Payment.NextStates(PaymentStatus.RequiresAction)
            .Should().BeEquivalentTo([PaymentStatus.Succeeded, PaymentStatus.Declined, PaymentStatus.Expired]);

    [Fact]
    [Trait("BR", "BR-PAY-05")]
    public void BR_PAY_05_a_gateway_error_is_retried_as_the_same_payment_or_ends_failed() =>
        PaymentTransitions.Payment.NextStates(PaymentStatus.Error)
            .Should().BeEquivalentTo([PaymentStatus.Processing, PaymentStatus.Failed]);

    [Fact]
    [Trait("BR", "BR-PAY-05")]
    public void BR_PAY_05_a_failed_payment_is_tried_again_as_the_same_payment_or_settled_by_the_gateway_status() =>
        PaymentTransitions.Payment.NextStates(PaymentStatus.Failed)
            .Should().Equal(PaymentStatus.Processing, PaymentStatus.Succeeded, PaymentStatus.Declined);

    [Fact]
    [Trait("BR", "BR-PAY-05")]
    public void BR_PAY_05_a_decline_is_never_retried() =>
        PaymentTransitions.Payment.CanMove(PaymentStatus.Declined, PaymentStatus.Processing).Should().BeFalse();
}
