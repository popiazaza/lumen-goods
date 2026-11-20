using System.Globalization;
using System.Text;
using System.Xml;

namespace KestrelOutfitters.StandIn.Catalogue;

/// <summary>Writes the feed document exactly in the published shape (element names, order, attributes).</summary>
internal static class CatalogueXml
{
    public static byte[] Write(IEnumerable<Product> products, DateTimeOffset generated, string pictureBaseUrl)
    {
        using var buffer = new MemoryStream();

        // Written by hand so the declaration reads exactly like the published sample: XmlWriter would write
        // encoding="utf-8". Same meaning, but a byte-for-byte comparison with the sample should not trip on it.
        buffer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"u8);
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = true,
            Indent = true,
            NewLineChars = "\n",
        };
        using (var xml = XmlWriter.Create(buffer, settings))
        {
            xml.WriteStartElement("Catalogue");
            xml.WriteAttributeString("generated", generated.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
            foreach (var product in products)
            {
                WriteProduct(xml, product, pictureBaseUrl);
            }

            xml.WriteEndElement();
        }

        return buffer.ToArray();
    }

    private static void WriteProduct(XmlWriter xml, Product product, string pictureBaseUrl)
    {
        xml.WriteStartElement("Product");
        xml.WriteAttributeString("ref", product.Ref);

        WriteLocalised(xml, "Name", "de", product.NameDe, asCData: false);
        WriteLocalised(xml, "Name", "en", product.NameEn, asCData: false);
        WriteLocalised(xml, "Desc", "de", product.DescDe, asCData: true);
        WriteLocalised(xml, "Desc", "en", product.DescEn, asCData: true);

        xml.WriteElementString("Group", product.Group);

        xml.WriteStartElement("Price");
        xml.WriteAttributeString("currency", "EUR");
        xml.WriteString(EuroPrice.Format(product.PriceEur));
        xml.WriteEndElement();

        // "Some have none": a product without pictures has no Pictures element at all.
        if (product.HasThumb || product.HasLarge)
        {
            xml.WriteStartElement("Pictures");
            if (product.HasThumb)
            {
                WritePicture(xml, "thumb", pictureBaseUrl, product.Ref, PictureSize.Thumb);
            }

            if (product.HasLarge)
            {
                WritePicture(xml, "large", pictureBaseUrl, product.Ref, PictureSize.Large);
            }

            xml.WriteEndElement();
        }

        xml.WriteStartElement("Specs");
        foreach (var spec in product.Specs)
        {
            xml.WriteStartElement("Spec");
            xml.WriteAttributeString("key", spec.Key);
            xml.WriteString(spec.Value);
            xml.WriteEndElement();
        }

        xml.WriteEndElement();

        xml.WriteElementString("Active", product.Active ? "Y" : "N");
        xml.WriteEndElement();
    }

    private static void WriteLocalised(XmlWriter xml, string element, string lang, string? text, bool asCData)
    {
        if (text is null)
        {
            return;
        }

        xml.WriteStartElement(element);
        xml.WriteAttributeString("lang", lang);
        if (asCData)
        {
            xml.WriteCData(text);
        }
        else
        {
            xml.WriteString(text);
        }

        xml.WriteEndElement();
    }

    private static void WritePicture(XmlWriter xml, string size, string baseUrl, string productRef, PictureSize pictureSize)
    {
        xml.WriteStartElement("Pic");
        xml.WriteAttributeString("size", size);
        xml.WriteString($"{baseUrl}/img/{ProductPictures.FileName(productRef, pictureSize)}");
        xml.WriteEndElement();
    }
}
