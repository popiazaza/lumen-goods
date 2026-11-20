using Lumen.Api.Catalog;

namespace Lumen.UnitTests.Catalog;

public sealed class TextInputTests
{
    [Theory]
    [InlineData("Waxed Canvas Weekender", true)]
    [InlineData("Café crème, 100% wool", true)]
    [InlineData("Emoji \U0001F600 pair", true)]
    [InlineData("Nul\0name", false)]
    [InlineData("Bell\a", false)]
    [InlineData("Tab\tseparated", false)]
    [InlineData("Two\nlines", false)]
    [InlineData("Escape\u001B[0m", false)]
    [InlineData("Delete\u007F", false)]
    public void Single_line_text_rejects_every_control_character(string value, bool valid)
    {
        TextInput.IsValidSingleLine(value).Should().Be(valid);
    }

    [Fact]
    public void Broken_utf16_is_rejected()
    {
        // Built from chars rather than theory data, which test runners may not be able to serialise.
        var lone = "Lone " + (char)0xD800 + " surrogate";
        var reversed = "Reversed " + (char)0xDE00 + (char)0xD83D + " pair";

        TextInput.IsValidSingleLine(lone).Should().BeFalse();
        TextInput.IsValidMultiLine(reversed).Should().BeFalse();
    }

    [Theory]
    [InlineData("First line.\r\nSecond line.\n\tIndented.", true)]
    [InlineData("Nul\0inside", false)]
    [InlineData("Form\ffeed", false)]
    [InlineData("Vertical\vtab", false)]
    public void Multi_line_text_allows_line_breaks_and_tabs_only(string value, bool valid)
    {
        TextInput.IsValidMultiLine(value).Should().Be(valid);
    }

    [Fact]
    public void A_search_with_a_nul_character_is_rejected_naming_q()
    {
        var ok = ProductSearchCriteria.TryParse(new ProductSearchParameters { Q = "socks\0" }, ["lumen"], out _, out var errors);

        ok.Should().BeFalse();
        errors.Should().ContainKey("q");
    }
}
