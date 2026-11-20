namespace Lumen.Domain.Payments;

/// <summary>
/// What the customer typed on the card form. <see cref="ExpiryYear"/> may be two digits (26) or four (2026).
/// <see cref="ToString"/> shows only the last 4 digits and leaves out the CVC and the name, so a logged or
/// interpolated card cannot leak them (BR-PAY-02).
/// </summary>
public sealed record CardDetails(string? Number, int ExpiryMonth, int ExpiryYear, string? Cvc, string? HolderName)
{
    /// <summary>"****0034": the last 4 digits of whatever was typed, or "****" when there are fewer.</summary>
    public static string MaskNumber(string? number)
    {
        var digits = number is null ? "" : new string([.. number.Where(char.IsAsciiDigit)]);
        return digits.Length >= 4 ? "****" + digits[^4..] : "****";
    }

    public override string ToString() =>
        $"CardDetails {{ Number = {MaskNumber(Number)}, ExpiryMonth = {ExpiryMonth}, ExpiryYear = {ExpiryYear} }}";
}

/// <summary>The validation outcome: the parsed card when valid, otherwise one message per invalid field.</summary>
/// <param name="Errors">Keyed by form field (<c>number</c>, <c>expiry</c>, <c>cvc</c>, <c>holderName</c>), ready for a validation problem response.</param>
public sealed record CardValidationResult(CardNumber? Card, IReadOnlyDictionary<string, string> Errors)
{
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Card))]
    public bool IsValid => Card is not null && Errors.Count == 0;
}

/// <summary>
/// BR-PAY-02: the server checks the card before the gateway is called: number (Luhn), expiry not in the past,
/// CVC length for the brand, and a name. A card that fails never reaches the gateway.
/// </summary>
public static class CardValidator
{
    public const int MaxHolderNameLength = 100;

    /// <param name="now">The server clock (pass <c>TimeProvider.GetUtcNow()</c>).</param>
    public static CardValidationResult Validate(CardDetails details, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!CardNumber.TryParse(details.Number, out var card))
        {
            errors["number"] = "Enter a valid card number.";
        }

        if (details.ExpiryMonth is < 1 or > 12 || details.ExpiryYear < 0)
        {
            errors["expiry"] = "Enter a valid expiry date.";
        }
        else if (IsExpired(details.ExpiryMonth, details.ExpiryYear, now))
        {
            errors["expiry"] = "This card has expired.";
        }

        if (!IsValidCvc(details.Cvc, card?.Brand))
        {
            errors["cvc"] = card?.Brand == CardBrand.Amex ? "Enter the 4-digit security code." : "Enter the 3-digit security code.";
        }

        if (string.IsNullOrWhiteSpace(details.HolderName) || details.HolderName.Trim().Length > MaxHolderNameLength)
        {
            errors["holderName"] = "Enter the name on the card.";
        }

        return new CardValidationResult(errors.Count == 0 ? card : null, errors);
    }

    /// <summary>A card is valid through the last day of its expiry month, so 11/2026 still works on 30 November 2026 (UTC).</summary>
    public static bool IsExpired(int month, int year, DateTimeOffset now)
    {
        var fullYear = year < 100 ? 2000 + year : year;
        var today = now.UtcDateTime;
        return fullYear < today.Year || (fullYear == today.Year && month < today.Month);
    }

    /// <summary>3 digits, or 4 for Amex. With no valid number (brand unknown), either length passes so only the number is reported.</summary>
    public static bool IsValidCvc(string? cvc, CardBrand? brand) =>
        cvc is not null
        && cvc.All(char.IsAsciiDigit)
        && brand switch
        {
            CardBrand.Amex => cvc.Length == 4,
            null => cvc.Length is 3 or 4,
            _ => cvc.Length == 3,
        };
}
