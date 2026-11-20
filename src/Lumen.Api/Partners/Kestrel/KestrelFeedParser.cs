using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Lumen.Api.Catalog;
using Lumen.Api.Partners.Mapping;
using Lumen.Api.Partners.Ports;
using Lumen.Domain.Catalog;

namespace Lumen.Api.Partners.Kestrel;

/// <summary>A parsed Kestrel feed. Products are in Lumen's terms; problems are per-product data issues.</summary>
internal sealed record KestrelFeed(DateTimeOffset? GeneratedUtc, IReadOnlyList<PartnerProductSnapshot> Products, IReadOnlyList<string> Problems);

/// <summary>
/// Streams <c>/feed/products.xml</c> into snapshots with constant memory per product. Safe by construction: DTDs are
/// refused (no entity expansion, no external entities), no resolver, and a character limit. A document that is not
/// well-formed to the very end (truncated) is rejected as a whole, so a broken download never looks like a smaller catalogue.
/// Mapping rules (spec feed rules + assumptions):
/// - Name/Desc chosen by <c>lang</c>: English, else German, else whatever is there (never "the first element");
///   Desc is sanitised to the allow-list.
/// - Price "1.249,00" (see <see cref="KestrelPrice"/>); anything else → no price (not sellable) + problem.
/// - Pictures: <c>large</c> preferred, else <c>thumb</c>, else any; at most one image. Active N (or missing) → inactive.
/// - Group not in the map → no category (not shown). Duplicate refs: the first wins.
/// Element names are matched by local name, so a namespaced feed reads the same.
/// </summary>
internal static class KestrelFeedParser
{
    public static async Task<KestrelFeed> ParseAsync(Stream stream, long maxCharacters, PartnerMappingContext context, CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxCharacters,
            MaxCharactersFromEntities = 1024,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = false,
        };

        var products = new List<PartnerProductSnapshot>();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? generated = null;

        try
        {
            using var reader = XmlReader.Create(stream, settings);
            await reader.MoveToContentAsync();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Catalogue")
            {
                throw new PartnerDataException($"Kestrel feed rejected: the root element is '{reader.LocalName}', not Catalogue.");
            }

            generated = ParseGenerated(reader.GetAttribute("generated"), problems);

            if (!reader.IsEmptyElement)
            {
                var depth = reader.Depth;
                await reader.ReadAsync();
                while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Product")
                    {
                        var element = (XElement)await XNode.ReadFromAsync(reader, cancellationToken);
                        if (ParseProduct(element, context, problems) is { } snapshot)
                        {
                            if (seen.Add(snapshot.SourceProductId))
                            {
                                products.Add(snapshot);
                            }
                            else
                            {
                                problems.Add($"Kestrel product {snapshot.SourceProductId} appears more than once in the feed; the first one is used.");
                            }
                        }
                    }
                    else
                    {
                        await reader.ReadAsync();
                    }
                }
            }

            // Read to the end: a document cut off after the last product must still fail here.
            while (await reader.ReadAsync())
            {
            }
        }
        catch (XmlException ex)
        {
            throw new PartnerDataException($"Kestrel feed rejected: not a complete, safe XML document ({ex.Message})", ex);
        }

        return new KestrelFeed(generated, products, problems);
    }

    private static DateTimeOffset? ParseGenerated(string? value, List<string> problems)
    {
        if (value is null)
        {
            return null;
        }

        // The sample carries an offset (+01:00); convert to UTC. A value without an offset is taken as UTC.
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        problems.Add($"Kestrel feed: generated '{value}' is not a timestamp.");
        return null;
    }

    private static PartnerProductSnapshot? ParseProduct(XElement product, PartnerMappingContext context, List<string> problems)
    {
        var reference = Attribute(product, "ref")?.Trim();
        if (string.IsNullOrEmpty(reference) || reference.Length > PartnerMappingContext.MaxSourceProductIdLength || reference.Contains(','))
        {
            problems.Add($"Kestrel product skipped: ref '{reference}' is missing, too long or contains a comma.");
            return null;
        }

        var name = Localised(product, "Name")?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            problems.Add($"Kestrel product {reference} skipped: no Name.");
            return null;
        }

        var itemProblems = new List<string>();
        var group = Child(product, "Group")?.Value.Trim() ?? "";
        var priceElement = Child(product, "Price");
        decimal? price = null;
        if (priceElement is null)
        {
            itemProblems.Add($"Kestrel product {reference}: no Price; shown as not sellable.");
        }
        else if (KestrelPrice.TryParse(priceElement.Value, out var amount))
        {
            price = amount;
        }
        else
        {
            itemProblems.Add($"Kestrel product {reference}: price '{priceElement.Value.Trim()}' is not in the format 1.234,56; shown as not sellable.");
        }

        var active = Child(product, "Active")?.Value.Trim();
        if (active is not ("Y" or "N"))
        {
            itemProblems.Add($"Kestrel product {reference}: Active is '{active}', not Y or N; treated as inactive.");
        }

        return new PartnerProductSnapshot(
            SourceProductId: reference,
            Name: name,
            Description: context.Sanitizer.Sanitize(Localised(product, "Desc")),
            Category: context.Categories.Map(group),
            PartnerCategory: group,
            Price: price,
            Currency: Attribute(priceElement, "currency") is { Length: > 0 } currency ? currency : CurrencyConverter.Eur,
            Images: PickPicture(product, context.BaseUri),
            Attributes: AttributeKeys.NormalizeAll(
                Children(Child(product, "Specs"), "Spec")
                    .Where(spec => Attribute(spec, "key") is not null)
                    .Select(spec => KeyValuePair.Create(Attribute(spec, "key")!, spec.Value)),
                context.AttributeSynonyms),
            IsActive: active == "Y",
            Stock: null,
            UpdatedUtc: null,
            Problems: itemProblems);
    }

    /// <summary>English if present, else German, else an element without a language, else any.</summary>
    private static string? Localised(XElement product, string elementName)
    {
        var variants = Children(product, elementName)
            .Select(e => (Lang: (Attribute(e, "lang") ?? e.Attribute(XNamespace.Xml + "lang")?.Value)?.Trim().ToLowerInvariant(), Text: e.Value))
            .Where(v => !string.IsNullOrWhiteSpace(v.Text))
            .ToList();

        return Pick(v => v.Lang is "en" || v.Lang?.StartsWith("en-", StringComparison.Ordinal) == true)
            ?? Pick(v => v.Lang is "de" || v.Lang?.StartsWith("de-", StringComparison.Ordinal) == true)
            ?? Pick(v => string.IsNullOrEmpty(v.Lang))
            ?? variants.FirstOrDefault().Text;

        string? Pick(Func<(string? Lang, string Text), bool> match) => variants.Where(match).Select(v => v.Text).FirstOrDefault();
    }

    private static List<ProductImage> PickPicture(XElement product, Uri baseUri)
    {
        var pictures = Children(Child(product, "Pictures"), "Pic")
            .Select(pic => (Size: Attribute(pic, "size")?.Trim().ToLowerInvariant(), Url: PartnerUrls.Resolve(baseUri, pic.Value)))
            .Where(p => p.Url is not null)
            .ToList();

        var chosen = pictures.FirstOrDefault(p => p.Size == "large").Url
            ?? pictures.FirstOrDefault(p => p.Size == "thumb").Url
            ?? pictures.FirstOrDefault().Url;

        return chosen is null ? [] : [new ProductImage(chosen.AbsoluteUri, IsPrimary: true)];
    }

    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static IEnumerable<XElement> Children(XElement? parent, string localName) =>
        parent?.Elements().Where(e => e.Name.LocalName == localName) ?? [];

    private static string? Attribute(XElement? element, string localName) =>
        element?.Attributes().FirstOrDefault(a => a.Name.LocalName == localName && a.Name.Namespace == XNamespace.None)?.Value;
}
