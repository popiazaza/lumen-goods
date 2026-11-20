using Lumen.Domain.Orders;

namespace Lumen.Api.Partners.Ports;

/// <summary>
/// Port (BR-ORD-01): places one order line with a partner and reads its decision, translated into one result shape
/// (<see cref="PartnerOrderResult"/>), so the partner order job never sees Harbor's JSON or Kestrel's XML. Called
/// only by the background job, after payment, outside any database transaction. Every call for a line carries the
/// same <see cref="PartnerOrderRequest.ExternalRef"/>, so a resend after a timeout never orders twice.
/// Adapters never throw for partner or transport failures: those are <see cref="PartnerOrderOutcome.RetryLater"/>.
/// </summary>
public interface IPartnerOrderGateway
{
    /// <summary>Partner code, equal to its product source (e.g. "harbor").</summary>
    string Partner { get; }

    /// <summary>
    /// True when the partner first accepts an order and decides later (Kestrel: a ticket, then CONFIRMED or REJECTED),
    /// so the job polls <see cref="GetStatusAsync"/>. False when submitting decides at once (Harbor).
    /// </summary>
    bool DecidesLater { get; }

    /// <summary>
    /// Sends the order. Mapping:
    /// <list type="table">
    /// <item><term>Harbor 201 ACCEPTED</term><description><see cref="PartnerOrderOutcome.Confirmed"/> with order_id</description></item>
    /// <item><term>Harbor 200 (reference already used)</term><description>Confirmed with the original order_id, <c>AlreadyExisted</c></description></item>
    /// <item><term>Harbor 409 INSUFFICIENT_STOCK</term><description>Rejected, reason and <c>AvailableQuantity</c></description></item>
    /// <item><term>Harbor 422 INVALID_ITEM, 400 INVALID_REQUEST</term><description>Rejected with the partner's code and message</description></item>
    /// <item><term>Harbor 429</term><description>RetryLater with <c>RetryAfter</c></description></item>
    /// <item><term>Kestrel 202 OrderAck RECEIVED</term><description><see cref="PartnerOrderOutcome.Accepted"/> with the ticket</description></item>
    /// <item><term>Kestrel 409 DUPLICATE_REFERENCE</term><description>Accepted with the original ticket (adopted), <c>AlreadyExisted</c></description></item>
    /// <item><term>Kestrel 400 INVALID_REQUEST</term><description>Rejected (unknown ref or malformed request)</description></item>
    /// <item><term>401, 5xx, timeout, network error</term><description>RetryLater: nothing is known, resend with the same reference</description></item>
    /// </list>
    /// </summary>
    Task<PartnerOrderResult> SubmitAsync(PartnerOrderRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the decision on an accepted order (only when <see cref="DecidesLater"/>; Harbor has no status endpoint and
    /// throws <see cref="NotSupportedException"/>). Kestrel <c>GET /orders/{ticket}</c>: RECEIVED → Accepted,
    /// CONFIRMED → Confirmed, REJECTED → Rejected with its reason (e.g. <c>OUT_OF_STOCK:K-7731</c>); 404 → NotFound;
    /// 5xx or timeout → RetryLater.
    /// </summary>
    /// <param name="partnerOrderId">The ticket from <see cref="PartnerOrderResult.PartnerOrderId"/>.</param>
    Task<PartnerOrderResult> GetStatusAsync(string partnerOrderId, CancellationToken cancellationToken);
}

/// <summary>One order line to place with a partner (one partner order per line).</summary>
/// <param name="ExternalRef"><c>{orderNumber}-{lineNumber}</c>: Harbor's <c>external_ref</c>, Kestrel's <c>reference</c>.</param>
/// <param name="SourceProductId">Harbor item_code or Kestrel ref.</param>
public sealed record PartnerOrderRequest(string ExternalRef, string SourceProductId, int Quantity, PartnerShipTo ShipTo);

/// <summary>
/// The delivery address in the fields both formats need. Harbor sends it as <c>ship_to</c> unchanged; Kestrel's
/// <c>Delivery</c> has no region or second line, so Street = "line1, line2" and the region is left out (A-28).
/// </summary>
/// <param name="CountryIso2">ISO 3166-1 alpha-2, e.g. "US".</param>
public sealed record PartnerShipTo(
    string Name,
    string Line1,
    string? Line2,
    string City,
    string Region,
    string PostalCode,
    string CountryIso2);

public enum PartnerOrderOutcome
{
    /// <summary>The partner has the order and will supply it (Harbor 201/200, Kestrel CONFIRMED).</summary>
    Confirmed,

    /// <summary>The partner took the order and will decide later (Kestrel 202 RECEIVED, 409 with ticket, or a RECEIVED poll).</summary>
    Accepted,

    /// <summary>The partner will not supply it; the line is refunded (BR-ORD-03).</summary>
    Rejected,

    /// <summary>No answer to act on (rate limit, partner or network error, timeout); try again later with the same reference.</summary>
    RetryLater,

    /// <summary>The partner does not know the ticket (Kestrel 404). Resubmitting the same reference returns it (409 with the ticket).</summary>
    NotFound,
}

/// <summary>What a partner said about one partner order, in Lumen's terms.</summary>
/// <param name="PartnerOrderId">Harbor order_id or Kestrel ticket, when the partner gave one.</param>
/// <param name="Reason">The partner's code and message for a rejection (e.g. "INSUFFICIENT_STOCK: 1 available", "OUT_OF_STOCK:K-7731").</param>
/// <param name="AlreadyExisted">The partner already had an order with this reference (Harbor 200, Kestrel 409): success, not a new order.</param>
/// <param name="AvailableQuantity">Harbor 409: units the partner has.</param>
/// <param name="RetryAfter">RetryLater: how long the partner asked Lumen to wait (Harbor 429 Retry-After), if it said.</param>
/// <param name="Error">RetryLater and NotFound: what went wrong, for the partner order's LastError and the admin pages.</param>
/// <param name="HttpStatus">The partner's HTTP status, when it answered at all.</param>
/// <param name="OutcomeUnknown">
/// BR-ORD-04: after this call the partner may hold an order for this reference although Lumen cannot tell. RetryLater:
/// the request may have been acted on (a timeout after sending, a 5xx, an answer Lumen could not read); false when it
/// never reached the partner or was refused without being acted on (connection refused, name not resolved, open
/// circuit, 429, 401). Rejected: the rejection does not rule out an order an earlier send of the same reference
/// created (Kestrel's 400 says nothing about a reference it may already have; Harbor answers a used reference with
/// the original order before judging it, so its rejections are final).
/// </param>
public sealed record PartnerOrderResult(
    PartnerOrderOutcome Outcome,
    string? PartnerOrderId = null,
    string? Reason = null,
    bool AlreadyExisted = false,
    int? AvailableQuantity = null,
    TimeSpan? RetryAfter = null,
    string? Error = null,
    int? HttpStatus = null,
    bool OutcomeUnknown = false)
{
    /// <summary>
    /// RetryLater for a send that failed in transport (<see cref="PartnerException"/> from the partner client): the
    /// outcome is unknown unless the request provably never reached the partner (connection or TLS not established,
    /// name not resolved, circuit open, client-side rate limit queue full).
    /// </summary>
    public static PartnerOrderResult SendFailed(PartnerException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var neverSent = failure.InnerException switch
        {
            Polly.CircuitBreaker.BrokenCircuitException => true,
            Polly.RateLimiting.RateLimiterRejectedException => true,
            HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError } => true,
            _ => false,
        };
        return new PartnerOrderResult(PartnerOrderOutcome.RetryLater, Error: failure.Message, HttpStatus: failure.StatusCode, OutcomeUnknown: !neverSent);
    }

    /// <summary>Whether an HTTP status means the partner may have acted on the request (5xx, 408, or a 2xx Lumen could not use).</summary>
    public static bool MayHaveActed(int status) => status is >= 500 or 408 or (>= 200 and < 300);

    /// <summary>
    /// The answer for <see cref="PartnerOrderUpdates.ApplyPartnerAnswer"/> (Accepted, Confirmed or Rejected), or null
    /// when the partner gave none (RetryLater, NotFound).
    /// </summary>
    public PartnerOrderStatus? Answer => Outcome switch
    {
        PartnerOrderOutcome.Confirmed => PartnerOrderStatus.Confirmed,
        PartnerOrderOutcome.Accepted => PartnerOrderStatus.Accepted,
        PartnerOrderOutcome.Rejected => PartnerOrderStatus.Rejected,
        _ => null,
    };
}
