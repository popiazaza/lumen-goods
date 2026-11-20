using Ganss.Xss;

namespace Lumen.Api.Partners.Mapping;

/// <summary>
/// Partner descriptions are partner-supplied content, "not guaranteed to be safe", and the storefront renders them as
/// HTML. Assumption A-21: they are reduced server-side to a small allow-list (p, br, b, strong, i, em, ul, ol, li)
/// with no attributes at all, so no URLs, event handlers or styles survive.
/// Two passes: the first removes everything outside the allow-list together with its content (script, style, iframe,
/// object...), except a list of harmless formatting tags; the second unwraps those, keeping their text (a heading or a
/// link's text stays readable, the tag goes). Plain text comes out with &amp;, &lt; and &gt; escaped.
/// </summary>
public sealed class DescriptionSanitizer
{
    public static readonly IReadOnlyList<string> AllowedTags = ["p", "br", "b", "strong", "i", "em", "ul", "ol", "li"];

    private static readonly string[] UnwrappedTags =
    [
        "a", "span", "div", "font", "center", "u", "s", "strike", "del", "ins", "mark", "small", "big", "sub", "sup",
        "abbr", "cite", "q", "code", "pre", "blockquote", "h1", "h2", "h3", "h4", "h5", "h6", "section", "article",
        "header", "footer", "table", "thead", "tbody", "tfoot", "tr", "td", "th", "caption", "dl", "dt", "dd",
    ];

    private readonly HtmlSanitizer _dropUnsafe = Create([.. AllowedTags, .. UnwrappedTags], keepChildNodes: false);
    private readonly HtmlSanitizer _unwrap = Create(AllowedTags, keepChildNodes: true);

    // HtmlSanitizer instances are not documented as safe for concurrent use; partner jobs can run in parallel.
    private readonly Lock _lock = new();

    public string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "";
        }

        lock (_lock)
        {
            return _unwrap.Sanitize(_dropUnsafe.Sanitize(html)).Trim();
        }
    }

    private static HtmlSanitizer Create(IEnumerable<string> tags, bool keepChildNodes)
    {
        var sanitizer = new HtmlSanitizer { KeepChildNodes = keepChildNodes, AllowDataAttributes = false };
        sanitizer.AllowedTags.Clear();
        foreach (var tag in tags)
        {
            sanitizer.AllowedTags.Add(tag);
        }

        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedClasses.Clear();
        sanitizer.AllowedAtRules.Clear();
        sanitizer.AllowedSchemes.Clear();
        sanitizer.UriAttributes.Clear();
        return sanitizer;
    }
}
