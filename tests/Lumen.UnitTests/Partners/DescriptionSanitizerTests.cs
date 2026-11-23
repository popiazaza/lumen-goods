using Lumen.Api.Partners.Mapping;

namespace Lumen.UnitTests.Partners;

/// <summary>
/// Assumption A-21: partner HTML is cut down to p, br, b, strong, i, em, ul, ol, li. Blocks and table cells that are
/// unwrapped leave a line break or a space, so their texts do not run together.
/// </summary>
public sealed class DescriptionSanitizerTests
{
    private static readonly DescriptionSanitizer Sanitizer = new();

    [Theory]
    // Kestrel K-7751, as its feed sends it.
    [InlineData("<p>Leather hiking boots with a Vibram sole.</p><table><tr><th>Weight</th><td>1.4 kg</td></tr></table>",
        "<p>Leather hiking boots with a Vibram sole.</p>Weight 1.4 kg")]
    [InlineData("<table><thead><tr><th>Size</th><th>Weight</th></tr></thead><tbody><tr><td>M</td><td>1.4 kg</td></tr><tr><td>L</td><td>1.5 kg</td></tr></tbody></table><p>Resoleable.</p>",
        "Size Weight<br>M 1.4 kg<br>L 1.5 kg<p>Resoleable.</p>")]
    [InlineData("<table>\n  <tr>\n    <td>Drop</td>\n    <td>8 mm</td>\n  </tr>\n  <tr><td>Stack</td><td>30 mm</td></tr>\n</table>",
        "Drop\n    8 mm\n  <br>\n  Stack 30 mm")]
    [InlineData("Intro<table><tr><td>A</td><td>B</td></tr></table>Outro", "Intro<br>A B<br>Outro")]
    [InlineData("<h2>Care</h2><div>Wipe clean.</div><div>Air dry.</div>", "Care<br>Wipe clean.<br>Air dry.")]
    [InlineData("<dl><dt>Fill</dt><dd>800 down</dd><dt>Shell</dt><dd>Nylon</dd></dl>", "Fill<br>800 down<br>Shell<br>Nylon")]
    public void Unwrapped_cells_and_rows_keep_their_texts_apart(string html, string expected) =>
        Sanitizer.Sanitize(html).Should().Be(expected);

    [Theory]
    [InlineData("<p>Read <a href=\"https://x.test\">the guide</a> first.</p>", "<p>Read the guide first.</p>")]
    [InlineData("<p>Size <span>M</span>.</p>", "<p>Size M.</p>")]
    [InlineData("<ul><li>Light</li><li>Warm</li></ul>", "<ul><li>Light</li><li>Warm</li></ul>")]
    [InlineData("<p>One</p><div>Two</div><p>Three</p>", "<p>One</p>Two<p>Three</p>")]
    public void Inline_tags_and_kept_blocks_get_no_extra_separator(string html, string expected) =>
        Sanitizer.Sanitize(html).Should().Be(expected);

    [Fact]
    public void Unsafe_content_is_still_removed_with_its_text()
    {
        var result = Sanitizer.Sanitize("<table><tr><td>Safe</td><td><script>alert(1)</script></td></tr></table><p onclick=\"x()\">Text</p>");

        result.Should().Be("Safe <p>Text</p>");
    }
}
