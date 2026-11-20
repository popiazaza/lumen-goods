using Lumen.Domain.Catalog;

namespace Lumen.UnitTests.Catalog;

public sealed class AttributeKeysTests
{
    [Theory]
    [InlineData("colour", "color")]      // Harbor props
    [InlineData("Colour", "color")]      // Kestrel Spec keys
    [InlineData("Size", "size")]
    [InlineData("Material", "material")]
    [InlineData("Fill Power", "fillPower")]
    [InlineData("fill_power", "fillPower")]
    [InlineData("waterproofRating", "waterproofRating")]
    [InlineData("  Pack Volume  ", "packVolume")]
    public void Produces_lower_camel_case_english_keys(string raw, string expected) =>
        AttributeKeys.Normalize(raw).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  -- ")]
    public void Unusable_keys_are_dropped(string? raw) =>
        AttributeKeys.Normalize(raw).Should().BeNull();

    [Fact]
    public void When_two_keys_collide_the_first_wins_and_blank_values_are_dropped() =>
        AttributeKeys.NormalizeAll([new("Colour", "Charcoal"), new("colour", "Olive"), new("Size", " "), new("Size ", "M")])
            .Should().BeEquivalentTo(new Dictionary<string, string> { ["color"] = "Charcoal", ["size"] = "M" });

    [Fact]
    public void Synonyms_are_configurable() =>
        AttributeKeys.Normalize("Farbe", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["farbe"] = "color" })
            .Should().Be("color");
}
