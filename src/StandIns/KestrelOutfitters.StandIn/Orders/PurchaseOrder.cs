namespace KestrelOutfitters.StandIn.Orders;

internal enum OrderState
{
    Received,
    Confirmed,
    Rejected,
}

/// <summary>A purchase order as Kestrel keeps it: received, then decided once its decision time has passed.</summary>
internal sealed class PurchaseOrder
{
    public int Id { get; init; }

    public required string Ticket { get; init; }

    /// <summary>Lumen's reference; unique, which is what makes a resubmission a 409 DUPLICATE_REFERENCE.</summary>
    public required string Reference { get; init; }

    public required List<OrderLine> Lines { get; init; }

    public required DeliveryAddress Delivery { get; init; }

    public OrderState State { get; private set; } = OrderState.Received;

    /// <summary>Set only when rejected, e.g. "OUT_OF_STOCK:K-7731".</summary>
    public string? Reason { get; private set; }

    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>ReceivedAt plus a random 10 to 30 seconds (contract).</summary>
    public DateTimeOffset DecideAt { get; init; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public void Confirm(DateTimeOffset now)
    {
        State = OrderState.Confirmed;
        DecidedAt = now;
    }

    public void Reject(string reason, DateTimeOffset now)
    {
        State = OrderState.Rejected;
        Reason = reason;
        DecidedAt = now;
    }

    /// <summary>The wire value of <see cref="State"/>.</summary>
    public string StateName => State switch
    {
        OrderState.Received => "RECEIVED",
        OrderState.Confirmed => "CONFIRMED",
        _ => "REJECTED",
    };
}

/// <summary>One line per ref; repeated Line elements for the same ref are summed on receipt.</summary>
internal sealed record OrderLine(string Ref, int Quantity);

internal sealed record DeliveryAddress(string Name, string Street, string City, string Postcode, string Country);
