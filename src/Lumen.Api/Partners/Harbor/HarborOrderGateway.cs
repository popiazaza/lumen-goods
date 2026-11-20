using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumen.Api.Partners.Ports;
using Lumen.Domain.Catalog;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Partners.Harbor;

/// <summary>
/// Harbor's ordering (<c>POST /v2/orders</c>) behind the order port (BR-ORD-01), relying on the published contract only:
/// - one Lumen line per Harbor order, so every request carries exactly one line and a rejection names that line;
/// - Harbor decides at once and has no status endpoint, so <see cref="GetStatusAsync"/> is not supported;
/// - the POST is never repeated by the HTTP pipeline (its retry is off for unsafe methods). The partner order job
///   resends it later with the same <c>external_ref</c>, which Harbor answers with 200 and the original order
///   instead of ordering again; so a 201 whose response was lost is recovered without a second order;
/// - <c>order_id</c> is opaque: stored as Harbor sent it (a string, or the text of a number), never parsed;
/// - a rejection (409, 422, 400) is final even after an earlier send whose answer was lost: the contract answers a used
///   external_ref with the original order, so a rejected resend proves Harbor has no order for it.
/// It never throws for partner or transport failures: those are <see cref="PartnerOrderOutcome.RetryLater"/>.
/// </summary>
internal sealed class HarborOrderGateway(
    IHttpClientFactory httpClients,
    IOptionsMonitor<HarborOptions> options,
    TimeProvider clock,
    ILogger<HarborOrderGateway> logger) : IPartnerOrderGateway
{
    /// <summary>A Retry-After longer than this is treated as this (a broken header must not park an order for days).</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);

    public string Partner => ProductSources.Harbor;

    public bool DecidesLater => false;

    public async Task<PartnerOrderResult> SubmitAsync(PartnerOrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (options.CurrentValue.BaseUri is null)
        {
            return new PartnerOrderResult(PartnerOrderOutcome.RetryLater, Error: "Harbor is not configured (Partners:Harbor:BaseUrl).");
        }

        var client = httpClients.CreateClient(HarborCatalogSource.HttpClientName);
        using var message = new HttpRequestMessage(HttpMethod.Post, "v2/orders")
        {
            Content = JsonContent.Create(HarborOrderRequestDto.From(request), options: HarborJson.Options),
        };

        try
        {
            using var response = await client.SendToPartnerAsync(message, HttpCompletionOption.ResponseContentRead, "Harbor", cancellationToken);
            var body = await PartnerHttpClients.ReadSnippetAsync(response, 4000, cancellationToken);
            return Interpret(response, body, request.ExternalRef);
        }
        catch (PartnerException ex)
        {
            // A timeout may mean Harbor has the order (unknown outcome); a refused connection or an open circuit means
            // it never got the request. Resending the same external_ref settles it (200 with the original, or a first 201).
            return PartnerOrderResult.SendFailed(ex);
        }
    }

    /// <exception cref="NotSupportedException">Always: Harbor's contract has no order status endpoint.</exception>
    public Task<PartnerOrderResult> GetStatusAsync(string partnerOrderId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Harbor decides when the order is placed and has no order status endpoint.");

    private PartnerOrderResult Interpret(HttpResponseMessage response, string body, string externalRef)
    {
        var status = (int)response.StatusCode;
        switch (response.StatusCode)
        {
            case HttpStatusCode.Created:
            case HttpStatusCode.OK:
            {
                // 201: created, stock reduced. 200: this external_ref was used before; the body is the original order.
                var orderId = ReadOrderId(body);
                if (orderId is null)
                {
                    logger.LogWarning("Harbor answered {Status} for {ExternalRef} without a readable order_id; the order exists, its id is unknown",
                        status, externalRef);
                }

                return new PartnerOrderResult(PartnerOrderOutcome.Confirmed, PartnerOrderId: orderId,
                    AlreadyExisted: response.StatusCode == HttpStatusCode.OK, HttpStatus: status);
            }

            case HttpStatusCode.Conflict:
            {
                // INSUFFICIENT_STOCK is the contract's 409; nothing is ordered. Any other 409 code is refused all the same.
                var error = HarborErrorBody.Read(body);
                var reason = error.Code == "INSUFFICIENT_STOCK" && error.Available is { } available
                    ? $"INSUFFICIENT_STOCK: {available.ToString(CultureInfo.InvariantCulture)} available"
                    : error.Describe("CONFLICT");
                return new PartnerOrderResult(PartnerOrderOutcome.Rejected, Reason: reason, AvailableQuantity: error.Available, HttpStatus: status);
            }

            case HttpStatusCode.UnprocessableEntity:
                // INVALID_ITEM: unknown, discontinued or unpriced. Nothing is ordered.
                return new PartnerOrderResult(PartnerOrderOutcome.Rejected, Reason: HarborErrorBody.Read(body).Describe("INVALID_ITEM"), HttpStatus: status);

            case HttpStatusCode.BadRequest:
                return new PartnerOrderResult(PartnerOrderOutcome.Rejected, Reason: HarborErrorBody.Read(body).Describe("INVALID_REQUEST"), HttpStatus: status);

            case HttpStatusCode.TooManyRequests:
            {
                var retryAfter = RetryAfter(response);
                return new PartnerOrderResult(PartnerOrderOutcome.RetryLater, RetryAfter: retryAfter,
                    Error: $"Harbor POST /v2/orders returned 429 (rate limited{(retryAfter is { } wait ? $", Retry-After {wait.TotalSeconds:0} s" : "")}).",
                    HttpStatus: status);
            }

            default:
                // 401/403 (credentials), 404 (wrong base URL) and 3xx were refused without being acted on; a 5xx, 408 or
                // a 2xx the contract does not use may have created the order (unknown outcome). Resending the same
                // external_ref is always safe.
                var detail = HarborErrorBody.Read(body);
                var suffix = detail.Code is null ? "" : $" ({detail.Describe(detail.Code)})";
                return new PartnerOrderResult(PartnerOrderOutcome.RetryLater,
                    Error: $"Harbor POST /v2/orders returned {status} {response.ReasonPhrase}{suffix}.", HttpStatus: status,
                    OutcomeUnknown: PartnerOrderResult.MayHaveActed(status));
        }
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan? wait = header?.Delta ?? (header?.Date is { } date ? date - clock.GetUtcNow() : null);
        if (wait is null)
        {
            return null;
        }

        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaxRetryAfter ? MaxRetryAfter : wait;
    }

    private static string? ReadOrderId(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("order_id", out var id))
            {
                var text = id.ValueKind switch
                {
                    JsonValueKind.String => id.GetString(),
                    JsonValueKind.Number => id.GetRawText(),
                    _ => null,
                };
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
        }
        catch (JsonException)
        {
            // The status code already says the order exists; only its id is unknown.
        }

        return null;
    }
}

/// <summary>Harbor's <c>POST /v2/orders</c> body, written from the contract (Lumen shares no code with the stand-in).</summary>
internal sealed record HarborOrderRequestDto(
    [property: JsonPropertyName("external_ref")] string ExternalRef,
    [property: JsonPropertyName("lines")] IReadOnlyList<HarborOrderLineDto> Lines,
    [property: JsonPropertyName("ship_to")] HarborShipToDto ShipTo)
{
    public static HarborOrderRequestDto From(PartnerOrderRequest request) => new(
        request.ExternalRef,
        [new HarborOrderLineDto(request.SourceProductId, request.Quantity)],
        new HarborShipToDto(
            request.ShipTo.Name,
            request.ShipTo.Line1,
            string.IsNullOrWhiteSpace(request.ShipTo.Line2) ? null : request.ShipTo.Line2,
            request.ShipTo.City,
            request.ShipTo.Region,
            request.ShipTo.PostalCode,
            request.ShipTo.CountryIso2));
}

internal sealed record HarborOrderLineDto(
    [property: JsonPropertyName("item_code")] string ItemCode,
    [property: JsonPropertyName("qty")] int Qty);

/// <summary>The contract's <c>ship_to</c>; <c>line2</c> is sent as null when empty, as in the contract's example.</summary>
internal sealed record HarborShipToDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("line1")] string Line1,
    [property: JsonPropertyName("line2")] string? Line2,
    [property: JsonPropertyName("city")] string City,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("postal_code")] string PostalCode,
    [property: JsonPropertyName("country_iso2")] string CountryIso2);

/// <summary>Harbor's error body <c>{"error":{"code","message",...}}</c>, read defensively (it may be missing or HTML).</summary>
internal sealed record HarborErrorBody(string? Code, string? Message, int? Available)
{
    private static readonly HarborErrorBody Empty = new(null, null, null);

    public static HarborErrorBody Read(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return Empty;
            }

            return new HarborErrorBody(
                Text(error, "code"),
                Text(error, "message"),
                error.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.Number && available.TryGetInt32(out var count)
                    ? count
                    : null);
        }
        catch (JsonException)
        {
            return Empty;
        }

        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;
    }

    /// <summary>"CODE: message", using <paramref name="fallbackCode"/> when Harbor sent no code.</summary>
    public string Describe(string fallbackCode) => Message is null ? Code ?? fallbackCode : $"{Code ?? fallbackCode}: {Message}";
}
