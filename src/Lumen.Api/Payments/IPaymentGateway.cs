using Lumen.Domain.Payments;

namespace Lumen.Api.Payments;

/// <summary>
/// The card payment gateway, as Lumen sees it. Every call is keyed by the caller's id (the payment id for charges, the
/// refund id for refunds), so a repeated call returns the first result and never charges or refunds twice (BR-PAY-01 (c)).
/// Called only outside Lumen's database transactions. Implementations answer gateway failures as
/// <see cref="GatewayChargeStatus.Error"/> results; an exception means the call itself could not be made.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>Charges the card, or returns what the first call with this payment id got.</summary>
    Task<GatewayChargeResult> ChargeAsync(GatewayChargeRequest request, CancellationToken cancellationToken);

    /// <summary>Completes a charge waiting for its one-time code (card 0034, BR-PAY-03).</summary>
    Task<GatewayChargeResult> SubmitOneTimeCodeAsync(Guid paymentId, string code, CancellationToken cancellationToken);

    /// <summary>What the gateway knows about this payment id now; <see cref="GatewayChargeStatus.NotFound"/> when it never got the charge.</summary>
    Task<GatewayChargeResult> GetChargeAsync(Guid paymentId, CancellationToken cancellationToken);

    /// <summary>Refunds part or all of an approved charge, or returns what the first call with this refund id got.</summary>
    Task<GatewayRefundResult> RefundAsync(GatewayRefundRequest request, CancellationToken cancellationToken);
}

/// <param name="PaymentId">The idempotency key.</param>
/// <param name="Card">The validated card; held in memory for the call only, never stored.</param>
/// <param name="Amount">The order total (BR-CHK-01): never an amount the client sent.</param>
public sealed record GatewayChargeRequest(Guid PaymentId, CardNumber Card, decimal Amount);

public enum GatewayChargeStatus
{
    Approved,
    Declined,

    /// <summary>Waiting for the customer's one-time code until <see cref="GatewayChargeResult.OneTimeCodeExpiresUtc"/>.</summary>
    RequiresAction,

    /// <summary>Accepted and still running (the slow card while it waits).</summary>
    Processing,

    /// <summary>The gateway failed; nothing was charged. The same payment id may be sent again.</summary>
    Error,

    /// <summary>The gateway never received a charge with this id.</summary>
    NotFound,
}

/// <param name="DeclineReason">Set when <see cref="GatewayChargeStatus.Declined"/>.</param>
/// <param name="OneTimeCodeIssuedUtc">RequiresAction: when the code was issued, on the server clock.</param>
/// <param name="Error">Error: what went wrong, for the admin timeline.</param>
public sealed record GatewayChargeResult(
    GatewayChargeStatus Status,
    DeclineReason? DeclineReason = null,
    DateTimeOffset? OneTimeCodeIssuedUtc = null,
    DateTimeOffset? OneTimeCodeExpiresUtc = null,
    string? Error = null)
{
    public static GatewayChargeResult Failure(string error) => new(GatewayChargeStatus.Error, Error: error);
}

/// <param name="RefundId">The idempotency key.</param>
/// <param name="PaymentId">The charge to refund (its idempotency key).</param>
public sealed record GatewayRefundRequest(Guid RefundId, Guid PaymentId, decimal Amount);

public sealed record GatewayRefundResult(bool Succeeded, string? Error = null);
