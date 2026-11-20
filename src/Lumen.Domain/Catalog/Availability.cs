namespace Lumen.Domain.Catalog;

public enum AvailabilityStatus
{
    InStock,
    LowStock,
    OutOfStock,
}

/// <summary>Availability as the storefront format describes it: a status and, when known, the exact quantity.</summary>
public sealed record Availability(AvailabilityStatus Status, int? Quantity);

/// <summary>
/// BR-CAT-01: the storefront format's availability rules.
/// - When the quantity is known, the status follows from it: 0 → out of stock, 1 to <see cref="LowStockMax"/> → low stock,
///   above → in stock (so a partner's "LIMITED, 8 on hand" is in stock).
/// - When the quantity is unknown, the caller says what is known (e.g. Harbor AVAILABLE → in stock; Kestrel stock not yet
///   read → out of stock, never in stock without evidence), and the quantity stays null.
/// </summary>
public sealed class AvailabilityPolicy(int lowStockMax = AvailabilityPolicy.DefaultLowStockMax)
{
    public const int DefaultLowStockMax = 5;

    public int LowStockMax { get; } = lowStockMax >= 1
        ? lowStockMax
        : throw new ArgumentOutOfRangeException(nameof(lowStockMax), "Must be at least 1.");

    public Availability FromQuantity(int quantity) => quantity switch
    {
        <= 0 => new Availability(AvailabilityStatus.OutOfStock, 0),
        _ when quantity <= LowStockMax => new Availability(AvailabilityStatus.LowStock, quantity),
        _ => new Availability(AvailabilityStatus.InStock, quantity),
    };

    public Availability Describe(int? quantity, AvailabilityStatus whenQuantityUnknown) =>
        quantity is { } known ? FromQuantity(known) : new Availability(whenQuantityUnknown, null);
}
