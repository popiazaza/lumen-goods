using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json.Serialization;
using Lumen.Api.Infrastructure.Json;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;

namespace Lumen.Api.Payments;

/// <summary>
/// <c>POST /api/v1/orders/{number}/payments</c>: pay the order by card or from the wallet (one method per order,
/// BR-PAY-06). The amount is always the order total; the request carries none (BR-CHK-01). The card is validated
/// on the server first (BR-PAY-02): an invalid card is a 400 validation problem keyed <c>card.number</c>,
/// <c>card.expiry</c>, <c>card.cvc</c>, <c>card.name</c>, and never reaches the gateway.
/// Answers 202 with <see cref="PaymentStatusDto"/> (the charge runs independently of the request, BR-PAY-04), or 409
/// <c>urn:lumen:problem:payment-in-progress</c> when a payment is already running (BR-PAY-01).
/// </summary>
/// <param name="Card">Required for card, absent for wallet.</param>
/// <example>
/// <code>
/// { "method": "card", "card": { "number": "4242 0000 0000 0034", "name": "Ada Lovelace", "expiryMonth": 12, "expiryYear": 2028, "cvc": "123" } }
/// { "method": "wallet", "card": null }
/// </code>
/// </example>
public sealed record PayRequest([Required] PaymentMethod? Method, CardDetailsDto? Card);

/// <summary>
/// The card form. Never stored or logged: only brand and last 4 digits are kept, and <see cref="ToString"/> masks the
/// number and leaves out the CVC, so a logged request cannot leak them (BR-PAY-02).
/// </summary>
/// <param name="ExpiryYear">Two digits (28) or four (2028).</param>
public sealed record CardDetailsDto(string? Number, string? Name, int ExpiryMonth, int ExpiryYear, string? Cvc)
{
    public CardDetails ToDomain() => new(Number, ExpiryMonth, ExpiryYear, Cvc, Name);

    private bool PrintMembers(StringBuilder builder)
    {
        var digits = Number is null ? "" : new string(Number.Where(char.IsAsciiDigit).ToArray());
        builder.Append("Number = ").Append(digits.Length >= 4 ? "****" + digits[^4..] : "****")
            .Append(", ExpiryMonth = ").Append(ExpiryMonth)
            .Append(", ExpiryYear = ").Append(ExpiryYear);
        return true;
    }
}

/// <summary><c>POST /api/v1/orders/{number}/payments/current/one-time-code</c> (card 0034, BR-PAY-03).</summary>
/// <example><code>{ "code": "123456" }</code></example>
public sealed record OneTimeCodeRequest([Required, MaxLength(16)] string Code);

/// <summary>
/// The order's current payment, as the payment page shows it. Returned by Pay, by the one-time code step, and by
/// <c>GET /api/v1/orders/{number}/payments/current</c>, which the page polls while <see cref="Status"/> is
/// created or processing ("Confirming your payment…", BR-PAY-04): a refresh never loses or repeats the payment.
/// </summary>
/// <param name="Status">created | processing | requires_action | succeeded | declined | error | failed | expired.</param>
/// <param name="CardLast4">With <see cref="CardBrand"/>: "Visa ending 0034". Null for wallet payments.</param>
/// <param name="NeedsOneTimeCode">Show the code field (status requires_action and the code has not expired).</param>
/// <param name="OneTimeCodeExpiresUtc">Server time the code expires, for the countdown.</param>
/// <param name="TestOneTimeCode">The mock's code ("test code: 123456"), shown because no SMS is sent; null unless a code is needed.</param>
/// <param name="DeclineMessage">Customer wording (BR-PAY-07): "Insufficient funds", or the generic "Your card was declined" for a stolen card.</param>
/// <param name="AttemptsLeft">Payment attempts the order has left (5 per order, BR-PAY-03).</param>
/// <param name="CanRetry">Another attempt can start now (the order awaits payment, its hold lasts and attempts are left).</param>
/// <param name="OrderStatus">When paid, go to the confirmation page; when expired, the order can no longer be paid.</param>
/// <example>
/// <code>
/// { "paymentId": "0193a2c4-...", "orderNumber": "LG-7K3M9QX2", "method": "card", "status": "requires_action",
///   "amount": 75.46, "cardBrand": "visa", "cardLast4": "0034", "needsOneTimeCode": true,
///   "oneTimeCodeExpiresUtc": "2026-11-20T08:05:00Z", "testOneTimeCode": "123456", "declineMessage": null,
///   "attemptsLeft": 5, "canRetry": false, "orderStatus": "payment_processing", "holdExpiresUtc": "2026-11-20T08:15:00Z" }
/// </code>
/// </example>
public sealed record PaymentStatusDto(
    Guid PaymentId,
    string OrderNumber,
    PaymentMethod Method,
    PaymentStatus Status,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Amount,
    CardBrand? CardBrand,
    string? CardLast4,
    bool NeedsOneTimeCode,
    DateTime? OneTimeCodeExpiresUtc,
    string? TestOneTimeCode,
    string? DeclineMessage,
    int AttemptsLeft,
    bool CanRetry,
    OrderStatus OrderStatus,
    DateTime HoldExpiresUtc);

/// <summary>Problem types the payment endpoints return, so the UI can tell its 409s apart.</summary>
public static class PaymentProblemTypes
{
    /// <summary>409: a payment for this order is already running (BR-PAY-01); show its status instead.</summary>
    public const string PaymentInProgress = "urn:lumen:problem:payment-in-progress";

    /// <summary>409: the order cannot be paid any more: already paid, expired, or out of attempts.</summary>
    public const string OrderNotPayable = "urn:lumen:problem:order-not-payable";
}
