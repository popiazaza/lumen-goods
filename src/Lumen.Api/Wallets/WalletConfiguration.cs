using Lumen.Api.Identity;
using Lumen.Api.Orders;
using Lumen.Api.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lumen.Api.Wallets;

internal sealed class WalletConfiguration : IEntityTypeConfiguration<Wallet>
{
    public void Configure(EntityTypeBuilder<Wallet> builder)
    {
        builder.HasKey(w => w.CustomerId);
        builder.Property(w => w.CustomerId).ValueGeneratedNever();
        builder.HasOne<AppUser>().WithOne().HasForeignKey<Wallet>(w => w.CustomerId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(w => w.Balance).HasPrecision(12, 2);
        builder.Property(w => w.Version).IsRowVersion();

        // BR-PAY-06: the last line of defence behind the conditional debit; two orders paid at once cannot overdraw.
        builder.ToTable(t => t.HasCheckConstraint("CK_Wallets_BalanceNotNegative", "\"Balance\" >= 0"));
    }
}

internal sealed class WalletTransactionConfiguration : IEntityTypeConfiguration<WalletTransaction>
{
    private static readonly string Seed = nameof(WalletTransactionKind.Seed);
    private static readonly string PaymentKind = nameof(WalletTransactionKind.Payment);
    private static readonly string RefundKind = nameof(WalletTransactionKind.Refund);

    public void Configure(EntityTypeBuilder<WalletTransaction> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.HasOne<Wallet>().WithMany().HasForeignKey(t => t.CustomerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Order>().WithMany().HasForeignKey(t => t.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Payment>().WithMany().HasForeignKey(t => t.PaymentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Refund>().WithMany().HasForeignKey(t => t.RefundId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(t => t.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(t => t.Amount).HasPrecision(12, 2);
        builder.Property(t => t.BalanceAfter).HasPrecision(12, 2);

        // The wallet page lists the newest transactions first.
        builder.HasIndex(t => new { t.CustomerId, t.CreatedUtc });

        // Each payment debits once, each refund credits once, each wallet is seeded once, whatever is retried.
        builder.HasIndex(t => t.PaymentId).IsUnique().HasFilter($"\"Kind\" = '{PaymentKind}'")
            .HasDatabaseName("IX_WalletTransactions_PaymentId_Payment");
        builder.HasIndex(t => t.RefundId).IsUnique().HasFilter($"\"Kind\" = '{RefundKind}'")
            .HasDatabaseName("IX_WalletTransactions_RefundId_Refund");
        builder.HasIndex(t => t.CustomerId).IsUnique().HasFilter($"\"Kind\" = '{Seed}'")
            .HasDatabaseName("IX_WalletTransactions_CustomerId_Seed");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_WalletTransactions_KindMatchesAmount",
                $"(\"Kind\" = '{Seed}' AND \"Amount\" > 0)"
                + $" OR (\"Kind\" = '{PaymentKind}' AND \"Amount\" < 0 AND \"PaymentId\" IS NOT NULL AND \"OrderId\" IS NOT NULL)"
                + $" OR (\"Kind\" = '{RefundKind}' AND \"Amount\" > 0 AND \"RefundId\" IS NOT NULL AND \"OrderId\" IS NOT NULL)");
            t.HasCheckConstraint("CK_WalletTransactions_BalanceAfterNotNegative", "\"BalanceAfter\" >= 0");
        });
    }
}
