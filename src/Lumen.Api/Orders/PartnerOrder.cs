using Lumen.Domain.Orders;

namespace Lumen.Api.Orders;

/// <summary>
/// The order Lumen places with a partner for one partner line (BR-ORD-01). Written (Pending) in the transaction that
/// marks the order Paid, never before; sent and polled by a background job outside any transaction. Status changes go
/// through <see cref="OrderTransitions.PartnerOrder"/> and <see cref="PartnerOrderUpdates"/> (which also moves the line).
/// </summary>
public sealed class PartnerOrder
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The line this partner order supplies. Unique: one partner order per line.</summary>
    public Guid OrderLineId { get; set; }

    public OrderLine? OrderLine { get; set; }

    /// <summary>The partner (a <c>ProductSources</c> value, e.g. "harbor").</summary>
    public required string Source { get; set; }

    /// <summary>
    /// <c>{orderNumber}-{lineNumber}</c>, e.g. <c>LG-7K3M9QX2-2</c>: Harbor's <c>external_ref</c>, Kestrel's
    /// <c>reference</c>. Unique, and reused on every retry, so a resend never orders twice.
    /// </summary>
    public required string ExternalRef { get; set; }

    /// <summary>
    /// The longest partner order id or ticket Lumen stores. Partner ids are opaque and never cut: a longer one is
    /// refused (recorded as an error) rather than stored shortened.
    /// </summary>
    public const int MaxPartnerOrderIdLength = 200;

    /// <summary>The partner's id for it once known: Harbor <c>order_id</c> (H-10001) or Kestrel ticket (KT-5521). Opaque, stored exactly.</summary>
    public string? PartnerOrderId { get; set; }

    public PartnerOrderStatus Status { get; set; } = OrderTransitions.PartnerOrder.Initial;

    /// <summary>The partner's reason for a rejection (e.g. <c>INSUFFICIENT_STOCK</c>, <c>OUT_OF_STOCK:K-7731</c>) or why it was given up.</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Calls to the partner so far: every send (and resend) of the order plus every status check (Kestrel polls its
    /// ticket until it decides), whatever their outcome. Counted when the job claims the call, so one accepted Kestrel
    /// order usually shows 1 send + its polls. The admin page labels it "Calls (sends + status checks)".
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>
    /// When the job should next send (Pending, Submitted) or poll (Accepted) it; null when nothing is due (final).
    /// The job claims due rows with the (Status, NextAttemptUtc) index.
    /// </summary>
    public DateTimeOffset? NextAttemptUtc { get; set; }

    /// <summary>
    /// Set while a job instance is calling the partner for it (claimed, answer not saved yet), until the claim expires
    /// (<c>PartnerOrders:ClaimTimeout</c>). An admin cannot resolve the line meanwhile (BR-ORD-04), so a decision never
    /// crosses a send that is already on its way.
    /// </summary>
    public DateTimeOffset? ClaimedUntilUtc { get; set; }

    /// <summary>The last transport or partner error, for the admin order page.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// BR-ORD-04: some send of this partner order may have reached the partner and been acted on, but Lumen never
    /// learned the result (a timeout after sending, a 5xx, a crash before the answer was saved). The partner may hold the
    /// order, so it is never given up and refunded automatically: at the deadline its line goes to an admin. Set when a
    /// send is claimed and kept unless that send provably never reached the partner; sticky until the partner answers.
    /// </summary>
    public bool OutcomeUnknown { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>First sent to the partner.</summary>
    public DateTimeOffset? SubmittedUtc { get; set; }

    /// <summary>The partner took it (Kestrel RECEIVED); the 24-hour "needs attention" clock starts here (BR-ORD-04).</summary>
    public DateTimeOffset? AcceptedUtc { get; set; }

    /// <summary>Reached a final status: confirmed, rejected, given up or closed by an admin.</summary>
    public DateTimeOffset? DecidedUtc { get; set; }

    /// <summary>PostgreSQL xmin: the polling job and an admin resolving the line cannot both win.</summary>
    public uint Version { get; set; }
}
