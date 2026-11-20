using System.Text.RegularExpressions;
using Lumen.Api.Catalog;

namespace Lumen.Api.Checkout;

/// <summary>
/// The ship-to address rules for the launch country (A-51, <see cref="CheckoutOptions.Country"/>): the country must be
/// the one Lumen ships to; for the US, a USPS state, district or territory code and a 5-digit ZIP (ZIP+4 allowed).
/// Free text must be one line without control characters, so a partner order never carries a broken address.
/// Field lengths and presence are checked by the request's data annotations before this runs.
/// </summary>
internal static partial class ShippingAddressRules
{
    public static readonly IReadOnlySet<string> UsRegions = new HashSet<string>(StringComparer.Ordinal)
    {
        "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA", "HI", "ID", "IL", "IN", "IA", "KS", "KY", "LA", "ME", "MD",
        "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ", "NM", "NY", "NC", "ND", "OH", "OK", "OR", "PA", "RI", "SC",
        "SD", "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY", "DC", "PR", "GU", "VI", "AS", "MP",
    };

    /// <returns>Errors keyed like the request (<c>shippingAddress.postalCode</c>); empty when the address is valid.</returns>
    public static Dictionary<string, string[]> Validate(ShippingAddressDto address, string shipsTo)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Fail(string field, string message) => errors[$"shippingAddress.{field}"] = [message];

        foreach (var (field, value, required) in new[]
        {
            ("name", address.Name, true), ("line1", address.Line1, true), ("line2", address.Line2, false),
            ("city", address.City, true), ("region", address.Region, true), ("postalCode", address.PostalCode, true),
        })
        {
            if (required && string.IsNullOrWhiteSpace(value))
            {
                Fail(field, "This field is required.");
            }
            else if (value is not null && !TextInput.IsValidSingleLine(value))
            {
                Fail(field, "Use a single line without control characters.");
            }
        }

        var country = address.Country?.Trim().ToUpperInvariant();
        if (country != shipsTo)
        {
            Fail("country", $"We ship to {shipsTo} only.");
            return errors;
        }

        if (country == "US")
        {
            if (!errors.ContainsKey("shippingAddress.region") && !UsRegions.Contains(address.Region.Trim().ToUpperInvariant()))
            {
                Fail("region", "Enter a two-letter US state code, e.g. OR.");
            }

            if (!errors.ContainsKey("shippingAddress.postalCode") && !UsZip().IsMatch(address.PostalCode.Trim()))
            {
                Fail("postalCode", "Enter a 5-digit ZIP code, e.g. 97209.");
            }
        }

        return errors;
    }

    [GeneratedRegex("^[0-9]{5}(-[0-9]{4})?$")]
    private static partial Regex UsZip();
}
