using System.Globalization;
using System.Text.RegularExpressions;

namespace KestrelOutfitters.StandIn.Catalogue;

/// <summary>
/// Kestrel's price format: comma decimal separator, dot thousands separator, two decimals ("14,95", "1.249,00").
/// Built from an explicit <see cref="NumberFormatInfo"/>: the process runs with invariant globalization,
/// so no culture ("de-DE") can be relied on.
/// </summary>
internal static partial class EuroPrice
{
    private static readonly NumberFormatInfo KestrelNumbers = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = ".",
        NumberGroupSizes = [3],
    };

    public static string Format(decimal price) => price.ToString("#,##0.00", KestrelNumbers);

    /// <summary>
    /// Accepts only the contract format, so "14.95" is refused instead of silently read as 1.495,00.
    /// </summary>
    public static bool TryParse(string text, out decimal price)
    {
        price = 0;
        return ContractFormat().IsMatch(text)
            && decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, KestrelNumbers, out price)
            && price > 0;
    }

    [GeneratedRegex(@"^([0-9]{1,3}(\.[0-9]{3})+|[0-9]+),[0-9]{2}\z")]
    private static partial Regex ContractFormat();
}
