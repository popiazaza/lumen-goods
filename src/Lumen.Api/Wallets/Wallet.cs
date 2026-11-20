namespace Lumen.Api.Wallets;

/// <summary>
/// A customer's mock USD wallet (BR-PAY-06): one per customer, opened with a seed credit (seeded customer $2,000.00,
/// new customers $500.00, no top-up). The balance never goes negative (a check constraint behind the conditional
/// debit <c>UPDATE ... SET Balance = Balance - total WHERE Balance >= total</c>), and every change has a
/// <see cref="WalletTransaction"/> written in the same database transaction.
/// </summary>
public sealed class Wallet
{
    public Guid CustomerId { get; set; }

    public decimal Balance { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public uint Version { get; set; }
}

public enum WalletTransactionKind
{
    /// <summary>The opening balance, once per wallet.</summary>
    Seed,

    /// <summary>A wallet payment for an order (negative amount), once per payment.</summary>
    Payment,

    /// <summary>A refund credited back to the wallet (positive amount), once per refund (BR-ORD-03).</summary>
    Refund,
}

/// <summary>
/// The wallet's log: one row per balance change, amount signed (debits negative). Unique per payment and per refund,
/// so a retried wallet payment or refund can never move the balance twice.
/// </summary>
public sealed class WalletTransaction
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid CustomerId { get; set; }

    /// <summary>Negative for a payment, positive for a seed or a refund; never zero.</summary>
    public decimal Amount { get; set; }

    /// <summary>The balance right after this change (from the debit or credit <c>UPDATE ... RETURNING</c>).</summary>
    public decimal BalanceAfter { get; set; }

    public WalletTransactionKind Kind { get; set; }

    public Guid? OrderId { get; set; }

    public Guid? PaymentId { get; set; }

    public Guid? RefundId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
}
