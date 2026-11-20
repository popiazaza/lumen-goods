namespace Lumen.Domain.Orders;

/// <summary>
/// A partner order and its line after an update. <see cref="IgnoredBecause"/> is set when an answer arrived too late
/// to change anything; the caller logs it and carries on.
/// </summary>
public sealed record PartnerLineUpdate(PartnerOrderStatus PartnerOrder, OrderLineStatus Line, string? IgnoredBecause = null)
{
    public bool Ignored => IgnoredBecause is not null;
}

/// <summary>
/// Keeps a partner order and its order line in step, so neither can be left behind by the other.
/// - BR-ORD-01: partners answer late and more than once. A repeated answer changes nothing; an answer for a line or
///   partner order that is already final (e.g. an admin closed it, or it was given up) is ignored with a reason to
///   log, never an exception, so a late Kestrel CONFIRMED cannot break the polling job.
/// - BR-ORD-04: an admin resolving a line that needs attention closes its partner order too, so polling stops.
/// </summary>
public static class PartnerOrderUpdates
{
    /// <param name="answer">What the partner said: Accepted (Kestrel RECEIVED), Confirmed or Rejected.</param>
    /// <exception cref="InvalidOperationException">
    /// The answer does not fit a partner order that is still open (e.g. Confirmed for one never sent): a bug, not a late answer.
    /// </exception>
    public static PartnerLineUpdate ApplyPartnerAnswer(PartnerOrderStatus partnerOrder, OrderLineStatus line, PartnerOrderStatus answer)
    {
        if (answer is not (PartnerOrderStatus.Accepted or PartnerOrderStatus.Confirmed or PartnerOrderStatus.Rejected))
        {
            throw new ArgumentOutOfRangeException(nameof(answer), answer, "A partner answers only Accepted, Confirmed or Rejected.");
        }

        if (answer == partnerOrder)
        {
            return new PartnerLineUpdate(partnerOrder, line);
        }

        if (OrderTransitions.PartnerOrder.IsFinal(partnerOrder) || OrderTransitions.Line.IsFinal(line))
        {
            return new PartnerLineUpdate(partnerOrder, line,
                $"Late partner answer {answer} ignored: the partner order is already {partnerOrder} and the line {line}.");
        }

        var nextPartnerOrder = OrderTransitions.PartnerOrder.Move(partnerOrder, answer);
        var nextLine = answer switch
        {
            PartnerOrderStatus.Confirmed => OrderTransitions.Line.Move(line, OrderLineStatus.Confirmed),
            PartnerOrderStatus.Rejected => OrderTransitions.Line.Move(line, OrderLineStatus.Rejected), // refund it (RefundCalculator)
            _ => line, // Accepted: still waiting for the partner's decision
        };
        return new PartnerLineUpdate(nextPartnerOrder, nextLine);
    }

    /// <summary>BR-ORD-04: an admin confirms or rejects (and refunds) a line that needs attention; its partner order closes.</summary>
    /// <param name="decision">Confirmed (the goods are coming) or Rejected (refund the line).</param>
    public static PartnerLineUpdate ResolveByAdmin(PartnerOrderStatus partnerOrder, OrderLineStatus line, OrderLineStatus decision)
    {
        if (decision is not (OrderLineStatus.Confirmed or OrderLineStatus.Rejected))
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision, "An admin confirms or rejects the line.");
        }

        if (line != OrderLineStatus.NeedsAttention)
        {
            throw new InvalidOperationException($"Only a line that needs attention is resolved by an admin; this one is {line}.");
        }

        return new PartnerLineUpdate(
            OrderTransitions.PartnerOrder.Move(partnerOrder, PartnerOrderStatus.ClosedByAdmin),
            OrderTransitions.Line.Move(line, decision));
    }
}
