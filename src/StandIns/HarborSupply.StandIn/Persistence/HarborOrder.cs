namespace HarborSupply.StandIn.Persistence;

/// <summary>An accepted order. Only 201s are stored, so a 409 or 422 never uses up an external_ref.</summary>
internal sealed class HarborOrder
{
    /// <summary>The numeric part of order_id (H-10001, H-10002, ...).</summary>
    public int Number { get; init; }

    public required string ExternalRef { get; init; }

    public required string Status { get; init; }

    public DateTime CreatedAt { get; init; }

    /// <summary>The accepted request as JSON, kept for inspection: the contract has no endpoint that reads orders back.</summary>
    public required string RequestJson { get; init; }

    public string OrderId => $"H-{Number}";
}
