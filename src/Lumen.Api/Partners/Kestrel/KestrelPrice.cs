using System.Globalization;
using System.Text.RegularExpressions;

namespace Lumen.Api.Partners.Kestrel;

/// <summary>
/// Kestrel prices: comma decimal separator, dot thousands separator, exactly two decimals ("14,95", "1.249,00").
/// Parsed with an explicit <see cref="NumberFormatInfo"/>, never a culture (InvariantGlobalization is on, and a
/// culture's rules can change). Anything not in exactly this shape is rejected rather than guessed at: "12.5" or
/// "1,249.00" would silently become a different amount under a lenient parser.
/// </summary>
public static partial class KestrelPrice
{
    private static readonly NumberFormatInfo Format = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = ".",
        NumberGroupSizes = [3],
        NegativeSign = "-",
        PositiveSign = "+",
    };

    // [0-9] rather than \d: \d also matches non-ASCII digits.
    [GeneratedRegex(@"^[0-9]{1,3}(\.[0-9]{3})*,[0-9]{2}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Pattern();

    public static bool TryParse(string? text, out decimal amount)
    {
        amount = 0;
        var trimmed = text?.Trim();
        return trimmed is not null
            && Pattern().IsMatch(trimmed)
            && decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, Format, out amount);
    }
}
