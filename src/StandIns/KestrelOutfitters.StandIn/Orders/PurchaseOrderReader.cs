using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Net.Http.Headers;

namespace KestrelOutfitters.StandIn.Orders;

internal sealed record OrderRequest(string Reference, List<OrderLine> Lines, DeliveryAddress Delivery);

/// <summary>
/// Reads a PurchaseOrder body. Anything malformed becomes a message for
/// <c>&lt;Error code="INVALID_REQUEST" message="..."/&gt;</c>; whether the refs exist is checked by the caller.
/// </summary>
internal static class PurchaseOrderReader
{
    private static readonly string[] DeliveryFields = ["Name", "Street", "City", "Postcode", "Country"];

    // Partner input is untrusted: no DTDs (entity expansion), no external resolution, bounded size.
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 1_000_000,
    };

    public static async Task<(OrderRequest? Order, string? Error)> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase))
        {
            return (null, "Content-Type must be application/xml.");
        }

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(request.Body, ReaderSettings);
            document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
        }
        catch (XmlException ex)
        {
            return (null, $"The body is not well-formed XML: {ex.Message}");
        }

        return Parse(document);
    }

    private static (OrderRequest? Order, string? Error) Parse(XDocument document)
    {
        var root = document.Root;
        if (root is null || root.Name != "PurchaseOrder")
        {
            return (null, "The root element must be PurchaseOrder.");
        }

        var reference = (string?)root.Attribute("reference");
        if (string.IsNullOrWhiteSpace(reference))
        {
            return (null, "PurchaseOrder needs a non-empty reference attribute.");
        }

        // Ordered, so a rejection names the first short line in the order Lumen sent the lines.
        var quantities = new OrderedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in root.Elements("Line"))
        {
            var productRef = (string?)line.Attribute("ref");
            if (string.IsNullOrEmpty(productRef))
            {
                return (null, "Every Line needs a ref attribute.");
            }

            if (!int.TryParse((string?)line.Attribute("qty"), NumberStyles.None, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
            {
                return (null, $"Line {productRef}: qty must be a whole number greater than 0.");
            }

            var total = (quantities.TryGetValue(productRef, out var earlier) ? earlier : 0) + qty;
            quantities[productRef] = total;
            if (total > int.MaxValue)
            {
                return (null, $"Line {productRef}: total qty is too large.");
            }
        }

        if (quantities.Count == 0)
        {
            return (null, "PurchaseOrder needs at least one Line.");
        }

        var delivery = root.Element("Delivery");
        if (delivery is null)
        {
            return (null, "PurchaseOrder needs a Delivery element.");
        }

        var missing = DeliveryFields.Where(field => string.IsNullOrWhiteSpace(delivery.Element(field)?.Value)).ToList();
        if (missing.Count > 0)
        {
            return (null, $"Delivery needs a non-empty {string.Join(", ", missing)}.");
        }

        string Field(string name) => delivery.Element(name)!.Value.Trim();
        var address = new DeliveryAddress(Field("Name"), Field("Street"), Field("City"), Field("Postcode"), Field("Country"));
        var lines = quantities.Select(q => new OrderLine(q.Key, (int)q.Value)).ToList();
        return (new OrderRequest(reference, lines, address), null);
    }
}
