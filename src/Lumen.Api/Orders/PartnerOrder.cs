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

    /// <summary>The partner's id for it once known: Harbor <c>order_id</c> (H-10001) or Kestrel ticket (KT-5521).</summary>
    public string? PartnerOrderId { get; set; }

    public PartnerOrderStatus Status { get; set; } = OrderTransitions.PartnerOrder.Initial;

    /// <summary>The partner's reason for a rejection (e.g. <c>INSUFFICIENT_STOCK</c>, <c>OUT_OF_STOCK:K-7731</c>) or why it was given up.</summary>
    public string? Reason { get; set; }

    /// <summary>Calls made so far (submits and polls that failed or were answered).</summary>
    public int Attempts { get; set; }

    /// <summary>
    /// When the job should next send (Pending, Submitted) or poll (Accepted) it; null when nothing is due (final).
    /// The job claims due rows with the (Status, NextAttemptUtc) index.
    /// </summary>
    public DateTimeOffset? NextAttemptUtc { get; set; }

    /// <summary>The last transport or partner error, for the admin order page.</summary>
    public string? LastError { get; set; }

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
