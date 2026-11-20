using Lumen.Domain.Catalog;

namespace Lumen.UnitTests.Catalog;

public sealed class AvailabilityPolicyTests
{
    private readonly AvailabilityPolicy _policy = new();

    [Theory]
    [Trait("BR", "BR-CAT-01")]
    [InlineData(0, AvailabilityStatus.OutOfStock)]
    [InlineData(1, AvailabilityStatus.LowStock)]
    [InlineData(5, AvailabilityStatus.LowStock)]
    [InlineData(6, AvailabilityStatus.InStock)]
    [InlineData(8, AvailabilityStatus.InStock)] // a partner's "LIMITED, 8 on hand" is in stock by the storefront rules
    public void BR_CAT_01_status_follows_a_known_quantity(int quantity, AvailabilityStatus expected) =>
        _policy.FromQuantity(quantity).Should().Be(new Availability(expected, quantity));

    [Theory]
    [Trait("BR", "BR-CAT-01")]
    [InlineData(AvailabilityStatus.InStock)]    // e.g. Harbor AVAILABLE without on_hand
    [InlineData(AvailabilityStatus.OutOfStock)] // e.g. Kestrel stock not read yet: never "in stock" without evidence
    public void BR_CAT_01_unknown_quantity_keeps_the_given_status_and_a_null_quantity(AvailabilityStatus whenUnknown) =>
        _policy.Describe(null, whenUnknown).Should().Be(new Availability(whenUnknown, null));

    [Fact]
    [Trait("BR", "BR-CAT-01")]
    public void BR_CAT_01_the_low_stock_threshold_is_configurable() =>
        new AvailabilityPolicy(lowStockMax: 10).FromQuantity(8).Status.Should().Be(AvailabilityStatus.LowStock);
}
