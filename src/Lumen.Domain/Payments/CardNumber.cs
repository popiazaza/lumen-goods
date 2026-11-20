using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Lumen.Domain.Payments;

public enum CardBrand
{
    Visa,
    Mastercard,
    Amex,
    Other,
}

/// <summary>
/// BR-PAY-02: a card number that passed the server's checks: 12 to 19 digits (spaces and dashes allowed between
/// them) with a valid Luhn check digit. Only <see cref="Brand"/> and <see cref="Last4"/> may be stored or logged;
/// <see cref="ToString"/> never prints the full number.
/// </summary>
public sealed class CardNumber
{
    public const int MinLength = 12;
    public const int MaxLength = 19;

    private CardNumber(string digits)
    {
        Digits = digits;
        Brand = DetectBrand(digits);
    }

    /// <summary>The full number, digits only. For the (mock) gateway call; never persist or log it.</summary>
    public string Digits { get; }

    public CardBrand Brand { get; }

    public string Last4 => Digits[^4..];

    public static bool TryParse(string? input, [NotNullWhen(true)] out CardNumber? card)
    {
        card = null;
        if (input is null)
        {
            return false;
        }

        var digits = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
            }
            else if (c is not (' ' or '-'))
            {
                return false;
            }
        }

        if (digits.Length is < MinLength or > MaxLength || !PassesLuhn(digits.ToString()))
        {
            return false;
        }

        card = new CardNumber(digits.ToString());
        return true;
    }

    /// <summary>The Luhn (mod 10) check: from the right, double every second digit, subtract 9 from results above 9; the sum must end in 0.</summary>
    public static bool PassesLuhn(string digits)
    {
        if (string.IsNullOrEmpty(digits) || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }

    public override string ToString() => $"{Brand} ending {Last4}";

    // Issuer prefixes: Visa 4; Mastercard 51-55 and 2221-2720; Amex 34 and 37.
    private static CardBrand DetectBrand(string digits)
    {
        var first2 = int.Parse(digits.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        var first4 = int.Parse(digits.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture);
        return digits[0] switch
        {
            '4' => CardBrand.Visa,
            _ when first2 is 34 or 37 => CardBrand.Amex,
            _ when first2 is >= 51 and <= 55 || first4 is >= 2221 and <= 2720 => CardBrand.Mastercard,
            _ => CardBrand.Other,
        };
    }
}
