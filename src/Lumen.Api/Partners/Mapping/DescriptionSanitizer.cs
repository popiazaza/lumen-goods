using AngleSharp.Dom;
using Ganss.Xss;

namespace Lumen.Api.Partners.Mapping;

/// <summary>
/// Partner descriptions are partner-supplied content, "not guaranteed to be safe", and the storefront renders them as
/// HTML. Assumption A-21: they are reduced server-side to a small allow-list (p, br, b, strong, i, em, ul, ol, li)
/// with no attributes at all, so no URLs, event handlers or styles survive.
/// Two passes: the first removes everything outside the allow-list together with its content (script, style, iframe,
/// object...), except a list of harmless formatting tags; the second unwraps those, keeping their text (a heading or a
/// link's text stays readable, the tag goes). Plain text comes out with &amp;, &lt; and &gt; escaped.
/// Unwrapping a block (a table row, div, heading...) leaves a line break where it ended, and a table cell a space, so
/// "&lt;tr&gt;&lt;th&gt;Weight&lt;/th&gt;&lt;td&gt;1.4 kg&lt;/td&gt;&lt;/tr&gt;" reads "Weight 1.4 kg", not "Weight1.4 kg".
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

    /// <summary>Unwrapped tags whose end is a line break.</summary>
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "center", "pre", "blockquote", "h1", "h2", "h3", "h4", "h5", "h6", "section", "article", "header", "footer",
        "table", "tr", "caption", "dl", "dt", "dd",
    };

    /// <summary>Unwrapped tags whose end is a space (the cells of a row sit side by side).</summary>
    private static readonly HashSet<string> CellTags = new(StringComparer.OrdinalIgnoreCase) { "td", "th" };

    /// <summary>Kept tags that already start or end a line: no separator is needed next to them.</summary>
    private static readonly HashSet<string> LineTags = new(StringComparer.OrdinalIgnoreCase) { "p", "br", "ul", "ol", "li" };

    private readonly HtmlSanitizer _dropUnsafe = Create([.. AllowedTags, .. UnwrappedTags], keepChildNodes: false);
    private readonly HtmlSanitizer _unwrap = CreateUnwrapper();

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

    private static HtmlSanitizer CreateUnwrapper()
    {
        var sanitizer = Create(AllowedTags, keepChildNodes: true);
        sanitizer.RemovingTag += (_, e) => Separate(e.Tag);
        return sanitizer;
    }

    /// <summary>
    /// Before a block or cell is unwrapped, puts a separator on each side that touches other text. Tags are unwrapped
    /// outermost first, so a row's neighbours are the other rows and, for the last, whatever followed the table.
    /// </summary>
    private static void Separate(IElement tag)
    {
        var block = BlockTags.Contains(tag.LocalName);
        if (!block && !CellTags.Contains(tag.LocalName))
        {
            return;
        }

        if (NeedsSeparator(tag, before: true, block))
        {
            tag.Before(Separator(tag.Owner!, block));
        }

        if (NeedsSeparator(tag, before: false, block))
        {
            tag.After(Separator(tag.Owner!, block));
        }
    }

    private static INode Separator(IDocument document, bool block) =>
        block ? document.CreateElement("br") : document.CreateTextNode(" ");

    /// <summary>
    /// Whether the text on one side of the tag would run into its content: a block needs a break unless a line already
    /// ends there; a cell needs a space unless there is whitespace already.
    /// </summary>
    private static bool NeedsSeparator(IElement tag, bool before, bool block)
    {
        for (var sibling = Step(tag); sibling is not null; sibling = Step(sibling))
        {
            switch (sibling)
            {
                case IText text when string.IsNullOrWhiteSpace(text.Data):
                    if (!block)
                    {
                        return false;
                    }

                    continue;
                case IText text:
                    return block || !char.IsWhiteSpace(before ? text.Data[^1] : text.Data[0]);
                case IElement element:
                    return !LineTags.Contains(element.LocalName);
            }
        }

        return false;

        INode? Step(INode node) => before ? node.PreviousSibling : node.NextSibling;
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
