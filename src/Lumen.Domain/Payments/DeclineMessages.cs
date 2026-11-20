namespace Lumen.Domain.Payments;

public enum DeclineReason
{
    InsufficientFunds,
    CardReportedStolen,
    WrongOneTimeCode,
    OneTimeCodeExpired,
    InsufficientWalletBalance,

    /// <summary>
    /// BR-PAY-05: the payment ended Failed (the gateway errored twice) and the gateway's status check later said it was
    /// never charged. Final, so the order can be paid again with any method.
    /// </summary>
    GatewayErrorUnresolvedNotCharged,
}

/// <summary>What the customer is told, and what the admin's payment timeline records.</summary>
public sealed record DeclineMessage(string Customer, string Admin);

/// <summary>
/// BR-PAY-07: one table from decline reason to the customer's and the admin's wording. A stolen card is shown to
/// the customer only as the generic decline (telling a fraudster the card is flagged helps them); the admin sees
/// the real reason. Insufficient funds is named, so the customer can pick another card or the wallet.
/// </summary>
public static class DeclineMessages
{
    public const string GenericDecline = "Your card was declined";

    public static readonly IReadOnlyDictionary<DeclineReason, DeclineMessage> Table = new Dictionary<DeclineReason, DeclineMessage>
    {
        [DeclineReason.InsufficientFunds] = new("Insufficient funds", "Insufficient funds"),
        [DeclineReason.CardReportedStolen] = new(GenericDecline, "Card reported stolen"),
        [DeclineReason.WrongOneTimeCode] = new("The code is not correct", "Wrong one-time code"),
        [DeclineReason.OneTimeCodeExpired] = new("The code has expired", "One-time code expired"),
        [DeclineReason.InsufficientWalletBalance] = new("Your wallet balance does not cover this order", "Wallet balance below the order total"),
        [DeclineReason.GatewayErrorUnresolvedNotCharged] = new(
            "The payment could not be completed and you were not charged", "Gateway errors; status check: never charged"),
    };

    public static string ForCustomer(DeclineReason reason) =>
        Table.TryGetValue(reason, out var message) ? message.Customer : GenericDecline;

    public static string ForAdmin(DeclineReason reason) =>
        Table.TryGetValue(reason, out var message) ? message.Admin : reason.ToString();
}
