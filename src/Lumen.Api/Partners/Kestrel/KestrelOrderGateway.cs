using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Lumen.Api.Partners.Ports;
using Lumen.Domain.Catalog;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Partners.Kestrel;

/// <summary>
/// Kestrel's asynchronous ordering behind the order port (BR-ORD-01), relying on the published contract only:
/// - <c>POST /orders</c> takes a <c>PurchaseOrder</c> built with <see cref="XmlWriter"/> (so <c>&amp;</c>, <c>&lt;</c> and
///   quotes in names and addresses are escaped, never concatenated) with one line per Lumen line;
/// - 202 RECEIVED only means received: the job polls <c>GET /orders/{ticket}</c> until CONFIRMED or REJECTED;
/// - 409 DUPLICATE_REFERENCE carries the ticket of the order this reference already placed (a resend after a lost
///   answer): that ticket is adopted, it is not an error;
/// - tickets are opaque: stored as sent, escaped as one path segment when polled, never parsed;
/// - answers are parsed with DTDs prohibited and no resolver, and a size cap.
/// It never throws for partner or transport failures: those are <see cref="PartnerOrderOutcome.RetryLater"/>.
/// </summary>
internal sealed class KestrelOrderGateway(
    IHttpClientFactory httpClients,
    IOptionsMonitor<KestrelOptions> options,
    ILogger<KestrelOrderGateway> logger) : IPartnerOrderGateway
{
    public string Partner => ProductSources.Kestrel;

    public bool DecidesLater => true;

    public async Task<PartnerOrderResult> SubmitAsync(PartnerOrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (options.CurrentValue.BaseUri is null)
        {
            return NotConfigured();
        }

        // Exactly the contract's type: a strict Kestrel could refuse a charset parameter with a 400, which reads as a
        // rejection and refunds every Kestrel line. The XML declaration already says UTF-8.
        var content = new ByteArrayContent(KestrelOrderXml.PurchaseOrder(request));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        using var message = new HttpRequestMessage(HttpMethod.Post, "orders") { Content = content };

        try
        {
            using var response = await httpClients.CreateClient(KestrelCatalogSource.HttpClientName)
                .SendToPartnerAsync(message, HttpCompletionOption.ResponseContentRead, "Kestrel", cancellationToken);
            var status = (int)response.StatusCode;
            var answer = KestrelOrderXml.TryRead(await PartnerHttpClients.ReadSnippetAsync(response, KestrelOrderXml.MaxAnswerCharacters, cancellationToken));

            switch (response.StatusCode)
            {
                case HttpStatusCode.Accepted when Is(answer, "OrderAck") && Attribute(answer, "ticket") is { } ticket:
                    return new PartnerOrderResult(PartnerOrderOutcome.Accepted, PartnerOrderId: ticket, HttpStatus: status);

                case HttpStatusCode.Accepted:
                    // Received, but without a ticket there is nothing to poll. Resending the same reference returns
                    // 409 DUPLICATE_REFERENCE with the ticket.
                    return RetryLater($"Kestrel POST /orders returned 202 without a readable ticket for {request.ExternalRef}.", status, outcomeUnknown: true);

                case HttpStatusCode.Conflict when Is(answer, "Error") && Attribute(answer, "code") == "DUPLICATE_REFERENCE"
                                                  && Attribute(answer, "ticket") is { } ticket:
                    logger.LogInformation("Kestrel already has {ExternalRef} as ticket {Ticket}; adopting it", request.ExternalRef, ticket);
                    return new PartnerOrderResult(PartnerOrderOutcome.Accepted, PartnerOrderId: ticket, AlreadyExisted: true, HttpStatus: status);

                case HttpStatusCode.Conflict:
                    // A conflict says Kestrel may already have this reference: the outcome is unknown until a ticket comes back.
                    return RetryLater($"Kestrel POST /orders returned 409 without a DUPLICATE_REFERENCE ticket for {request.ExternalRef}.", status, outcomeUnknown: true);

                case HttpStatusCode.BadRequest:
                {
                    // Malformed request or unknown ref: Kestrel will not take this order however often it is sent. The
                    // contract does not say a used reference is checked first, so after an earlier send whose answer
                    // was lost this does not prove Kestrel has no order for it (OutcomeUnknown).
                    var code = Is(answer, "Error") ? Attribute(answer, "code") ?? "INVALID_REQUEST" : "INVALID_REQUEST";
                    var text = Is(answer, "Error") ? Attribute(answer, "message") : null;
                    return new PartnerOrderResult(PartnerOrderOutcome.Rejected, Reason: text is null ? code : $"{code}: {text}", HttpStatus: status,
                        OutcomeUnknown: true);
                }

                default:
                    return RetryLater($"Kestrel POST /orders returned {status} {response.ReasonPhrase}.", status,
                        outcomeUnknown: PartnerOrderResult.MayHaveActed(status));
            }
        }
        catch (PartnerException ex)
        {
            // A timeout may mean Kestrel has it (unknown outcome); a refused connection means it never got it. Resending
            // the same reference settles it (202 or 409 with the ticket).
            return PartnerOrderResult.SendFailed(ex);
        }
    }

    public async Task<PartnerOrderResult> GetStatusAsync(string partnerOrderId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partnerOrderId);
        if (options.CurrentValue.BaseUri is null)
        {
            return NotConfigured();
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, "orders/" + Uri.EscapeDataString(partnerOrderId));
        try
        {
            using var response = await httpClients.CreateClient(KestrelCatalogSource.HttpClientName)
                .SendToPartnerAsync(message, HttpCompletionOption.ResponseContentRead, "Kestrel", cancellationToken);
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new PartnerOrderResult(PartnerOrderOutcome.NotFound, PartnerOrderId: partnerOrderId,
                    Error: $"Kestrel does not know ticket {partnerOrderId} (404).", HttpStatus: status);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return RetryLater($"Kestrel GET /orders/{{ticket}} returned {status} {response.ReasonPhrase} for {partnerOrderId}.", status);
            }

            var answer = KestrelOrderXml.TryRead(await PartnerHttpClients.ReadSnippetAsync(response, KestrelOrderXml.MaxAnswerCharacters, cancellationToken));
            if (!Is(answer, "OrderStatus"))
            {
                return RetryLater($"Kestrel GET /orders/{{ticket}} for {partnerOrderId} answered without a readable OrderStatus.", status);
            }

            if (Attribute(answer, "ticket") is { } echoed && echoed != partnerOrderId)
            {
                return RetryLater($"Kestrel answered ticket {echoed} when asked for {partnerOrderId}.", status);
            }

            var state = Attribute(answer, "state");
            return state?.ToUpperInvariant() switch
            {
                "RECEIVED" => new PartnerOrderResult(PartnerOrderOutcome.Accepted, PartnerOrderId: partnerOrderId, HttpStatus: status),
                "CONFIRMED" => new PartnerOrderResult(PartnerOrderOutcome.Confirmed, PartnerOrderId: partnerOrderId, HttpStatus: status),
                "REJECTED" => new PartnerOrderResult(PartnerOrderOutcome.Rejected, PartnerOrderId: partnerOrderId,
                    Reason: Attribute(answer, "reason") ?? "REJECTED", HttpStatus: status),

                // Not in the contract (e.g. SHIPPED): not a decision Lumen understands, so keep asking and say so.
                _ => RetryLater($"Kestrel ticket {partnerOrderId} is in an unknown state '{state}'.", status),
            };
        }
        catch (PartnerException ex)
        {
            return RetryLater(ex.Message, ex.StatusCode);
        }
    }

    /// <summary>The answer is the contract's element <paramref name="name"/> (no namespace, as in the contract).</summary>
    private static bool Is([NotNullWhen(true)] XElement? answer, string name) =>
        answer is not null && answer.Name.NamespaceName.Length == 0 && answer.Name.LocalName == name;

    private static string? Attribute(XElement element, string name) =>
        element.Attribute(name)?.Value is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static PartnerOrderResult RetryLater(string error, int? status, bool outcomeUnknown = false) =>
        new(PartnerOrderOutcome.RetryLater, Error: error, HttpStatus: status, OutcomeUnknown: outcomeUnknown);

    private static PartnerOrderResult NotConfigured() =>
        new(PartnerOrderOutcome.RetryLater, Error: "Kestrel is not configured (Partners:Kestrel:BaseUrl).");
}

/// <summary>Kestrel's order documents: the <c>PurchaseOrder</c> Lumen sends and the small answers it reads back.</summary>
internal static class KestrelOrderXml
{
    /// <summary>An order answer is one element with a few attributes; anything larger is not one.</summary>
    public const int MaxAnswerCharacters = 64 * 1024;

    private static readonly XmlWriterSettings WriterSettings = new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = false,
        CheckCharacters = true,
    };

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxAnswerCharacters,
        MaxCharactersFromEntities = 1024,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    /// <summary>
    /// The contract's <c>PurchaseOrder</c>. Delivery has no second address line or region (A-28): Street is
    /// "line1, line2" and the region is not sent. Characters XML cannot carry (control characters) are dropped.
    /// </summary>
    public static byte[] PurchaseOrder(PartnerOrderRequest request)
    {
        var shipTo = request.ShipTo;
        var street = string.IsNullOrWhiteSpace(shipTo.Line2) ? shipTo.Line1 : $"{shipTo.Line1}, {shipTo.Line2}";

        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, WriterSettings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("PurchaseOrder");
            writer.WriteAttributeString("reference", Clean(request.ExternalRef));

            writer.WriteStartElement("Line");
            writer.WriteAttributeString("ref", Clean(request.SourceProductId));
            writer.WriteAttributeString("qty", request.Quantity.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();

            writer.WriteStartElement("Delivery");
            writer.WriteElementString("Name", Clean(shipTo.Name));
            writer.WriteElementString("Street", Clean(street));
            writer.WriteElementString("City", Clean(shipTo.City));
            writer.WriteElementString("Postcode", Clean(shipTo.PostalCode));
            writer.WriteElementString("Country", Clean(shipTo.CountryIso2));
            writer.WriteEndElement();

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return buffer.ToArray();
    }

    /// <summary>The root element of an answer, or null when it is empty, not XML, too large or carries a DTD.</summary>
    public static XElement? TryRead(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var reader = XmlReader.Create(new StringReader(text), ReaderSettings);
            return XElement.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>Drops characters XML 1.0 cannot represent (escaping does not help for those).</summary>
    public static string Clean(string value)
    {
        if (value.All(XmlConvert.IsXmlChar))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (XmlConvert.IsXmlChar(value[i]))
            {
                builder.Append(value[i]);
            }
            else if (i + 1 < value.Length && XmlConvert.IsXmlSurrogatePair(value[i + 1], value[i]))
            {
                builder.Append(value[i]).Append(value[i + 1]);
                i++;
            }
        }

        return builder.ToString();
    }
}
