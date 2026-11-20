using Lumen.Api.Checkout;

namespace Lumen.UnitTests.Checkout;

/// <summary>The launch country's ship-to rules (A-51): what a partner order will carry must be a deliverable US address.</summary>
public sealed class ShippingAddressRulesTests
{
    private static readonly ShippingAddressDto Valid = new("Ada Lovelace", "1200 NW Naito Pkwy", "Suite 4", "Portland", "OR", "97209", "US");

    [Theory]
    [InlineData("97209")]
    [InlineData("97209-1234")]
    public void A_us_address_with_a_state_code_and_zip_is_valid(string zip) =>
        ShippingAddressRules.Validate(Valid with { PostalCode = zip, Region = "or" }, "US").Should().BeEmpty();

    [Theory]
    [InlineData("region", "XX")]
    [InlineData("postalCode", "9720")]
    [InlineData("postalCode", "ABCDE")]
    [InlineData("city", "  ")]
    [InlineData("line1", "1200 NW\nNaito")]
    public void An_invalid_field_is_reported_under_its_own_key(string field, string value)
    {
        var address = field switch
        {
            "region" => Valid with { Region = value },
            "postalCode" => Valid with { PostalCode = value },
            "city" => Valid with { City = value },
            _ => Valid with { Line1 = value },
        };

        ShippingAddressRules.Validate(address, "US").Keys.Should().Equal($"shippingAddress.{field}");
    }

    [Fact]
    public void Only_the_launch_country_is_accepted() =>
        ShippingAddressRules.Validate(Valid with { Country = "CA", Region = "BC", PostalCode = "V6B 1A1" }, "US").Keys
            .Should().Equal("shippingAddress.country");
}
