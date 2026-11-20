namespace Lumen.Domain.Payments;

/// <summary>How the mock gateway answers for a card.</summary>
public enum CardBehaviour
{
    Approve,
    DeclineInsufficientFunds,
    DeclineStolen,

    /// <summary>Needs a one-time code (BR-PAY-03).</summary>
    RequireOneTimeCode,

    /// <summary>Approves after <see cref="PaymentOptions.SlowCardDelay"/> (BR-PAY-04).</summary>
    ApproveAfterDelay,

    /// <summary>Errors on the first call, approves when the same payment id is retried (BR-PAY-05).</summary>
    ErrorThenApprove,
}

/// <summary>
/// BR-PAY-02: the spec's test cards, as one table. The special numbers are matched first; any other number that
/// passed validation (Luhn) is approved.
/// </summary>
public static class CardBehaviours
{
    public static readonly IReadOnlyDictionary<string, CardBehaviour> TestCards =
        new Dictionary<string, CardBehaviour>(StringComparer.Ordinal)
        {
            ["4111111111111111"] = CardBehaviour.Approve,
            ["4242000000000018"] = CardBehaviour.DeclineInsufficientFunds,
            ["4242000000000026"] = CardBehaviour.DeclineStolen,
            ["4242000000000034"] = CardBehaviour.RequireOneTimeCode,
            ["4242000000000042"] = CardBehaviour.ApproveAfterDelay,
            ["4242000000000059"] = CardBehaviour.ErrorThenApprove,
        };

    public static CardBehaviour For(CardNumber card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return TestCards.GetValueOrDefault(card.Digits, CardBehaviour.Approve);
    }
}
