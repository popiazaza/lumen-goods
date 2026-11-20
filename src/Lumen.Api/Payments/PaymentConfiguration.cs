using Lumen.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lumen.Api.Payments;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    /// <summary>
    /// SQL filter for the payments that are not final in <see cref="PaymentTransitions.Payment"/> (Created, Processing,
    /// RequiresAction, Error, Failed), built from the table so the index can never drift from the lifecycle. Failed is
    /// included on purpose: Lumen cannot tell whether a Failed charge went through, so the only way on is the
    /// customer's Try again on the same payment id (BR-PAY-05), never a second payment next to it.
    /// </summary>
    public static readonly string NotFinalFilter = "\"Status\" IN (" + string.Join(", ",
        Enum.GetValues<PaymentStatus>().Where(s => !PaymentTransitions.Payment.IsFinal(s)).Select(s => $"'{s}'")) + ")";

    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.Method).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(p => p.DeclineReason).HasConversion<string>().HasMaxLength(64);
        builder.Property(p => p.CardBrand).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.CardLast4).HasMaxLength(4).IsFixedLength();
        builder.Property(p => p.Amount).HasPrecision(12, 2);
        builder.Property(p => p.Version).IsRowVersion();
        builder.Ignore(p => p.Snapshot);

        // BR-PAY-01 (b): at most one payment per order is still running; a second one cannot even be inserted.
        builder.HasIndex(p => p.OrderId).IsUnique().HasFilter(NotFinalFilter).HasDatabaseName("IX_Payments_OrderId_NotFinal");
        builder.HasIndex(p => new { p.OrderId, p.CreatedUtc });

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Payments_AmountNotNegative", "\"Amount\" >= 0");
            t.HasCheckConstraint("CK_Payments_AttemptNumber", "\"AttemptNumber\" >= 1");

            // BR-PAY-02: a card payment keeps brand + last 4 and nothing else; a wallet payment keeps no card data.
            t.HasCheckConstraint("CK_Payments_CardDataOnlyForCards",
                $"(\"Method\" = '{nameof(PaymentMethod.Card)}') = (\"CardLast4\" IS NOT NULL AND \"CardBrand\" IS NOT NULL)");
            t.HasCheckConstraint("CK_Payments_CardLast4IsFourDigits", "\"CardLast4\" IS NULL OR \"CardLast4\" ~ '^[0-9]{4}$'");
        });
    }
}

internal sealed class PaymentEventConfiguration : IEntityTypeConfiguration<PaymentEvent>
{
    public void Configure(EntityTypeBuilder<PaymentEvent> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.HasOne<Payment>().WithMany().HasForeignKey(e => e.PaymentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Orders.Order>().WithMany().HasForeignKey(e => e.OrderId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.Amount).HasPrecision(12, 2);
        builder.Property(e => e.Detail).HasMaxLength(500);

        // The admin order page reads one order's timeline in time order.
        builder.HasIndex(e => new { e.OrderId, e.OccurredUtc });
    }
}

internal sealed class MockGatewayChargeConfiguration : IEntityTypeConfiguration<MockGatewayCharge>
{
    public void Configure(EntityTypeBuilder<MockGatewayCharge> builder)
    {
        builder.HasKey(c => c.IdempotencyKey);
        builder.Property(c => c.IdempotencyKey).ValueGeneratedNever();

        builder.Property(c => c.CardBrand).HasConversion<string>().HasMaxLength(16);
        builder.Property(c => c.CardLast4).HasMaxLength(4).IsFixedLength();
        builder.Property(c => c.Behaviour).HasConversion<string>().HasMaxLength(32);
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(c => c.DeclineReason).HasConversion<string>().HasMaxLength(64);
        builder.Property(c => c.Amount).HasPrecision(12, 2);
        builder.Property(c => c.RefundedAmount).HasPrecision(12, 2);
        builder.Property(c => c.Version).IsRowVersion();

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_MockGatewayCharges_Amount", "\"Amount\" >= 0");
            t.HasCheckConstraint("CK_MockGatewayCharges_RefundedWithinAmount", "\"RefundedAmount\" >= 0 AND \"RefundedAmount\" <= \"Amount\"");
            t.HasCheckConstraint("CK_MockGatewayCharges_ErrorCount", "\"ErrorCount\" >= 0");
            t.HasCheckConstraint("CK_MockGatewayCharges_CardLast4IsFourDigits", "\"CardLast4\" ~ '^[0-9]{4}$'");
        });
    }
}

internal sealed class MockGatewayRefundConfiguration : IEntityTypeConfiguration<MockGatewayRefund>
{
    public void Configure(EntityTypeBuilder<MockGatewayRefund> builder)
    {
        builder.HasKey(r => r.IdempotencyKey);
        builder.Property(r => r.IdempotencyKey).ValueGeneratedNever();

        // Within the mock gateway only: a refund belongs to one of its charges.
        builder.HasOne<MockGatewayCharge>().WithMany().HasForeignKey(r => r.ChargeIdempotencyKey).OnDelete(DeleteBehavior.Restrict);
        builder.Property(r => r.Amount).HasPrecision(12, 2);
        builder.ToTable(t => t.HasCheckConstraint("CK_MockGatewayRefunds_AmountPositive", "\"Amount\" > 0"));
    }
}
