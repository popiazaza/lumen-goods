using System.Text.Json;
using Lumen.Api.Identity;
using Lumen.Api.Payments;
using Lumen.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lumen.Api.Orders;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();

        builder.Property(o => o.Number).HasMaxLength(16);
        builder.HasIndex(o => o.Number).IsUnique();

        // BR-PAY-01 (a): a repeated Place order with the same key finds the first order instead of creating another.
        builder.HasIndex(o => o.CheckoutKey).IsUnique();

        builder.HasOne<AppUser>().WithMany().HasForeignKey(o => o.CustomerId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(o => o.ShippingMethodForOwnItems).HasConversion<string>().HasMaxLength(16);

        builder.Property(o => o.Subtotal).HasPrecision(12, 2);
        builder.Property(o => o.ShippingTotal).HasPrecision(12, 2);
        builder.Property(o => o.TaxTotal).HasPrecision(12, 2);
        builder.Property(o => o.Total).HasPrecision(12, 2);
        builder.Property(o => o.RefundedTotal).HasPrecision(12, 2);

        builder.ComplexProperty(o => o.ShipTo, address =>
        {
            address.Property(a => a.Name).HasColumnName("ShipToName").HasMaxLength(100);
            address.Property(a => a.Line1).HasColumnName("ShipToLine1").HasMaxLength(200);
            address.Property(a => a.Line2).HasColumnName("ShipToLine2").HasMaxLength(200);
            address.Property(a => a.City).HasColumnName("ShipToCity").HasMaxLength(100);
            address.Property(a => a.Region).HasColumnName("ShipToRegion").HasMaxLength(100);
            address.Property(a => a.PostalCode).HasColumnName("ShipToPostalCode").HasMaxLength(20);
            address.Property(a => a.Country).HasColumnName("ShipToCountry").HasMaxLength(2);
        });

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Orders_AmountsNotNegative",
                "\"Subtotal\" >= 0 AND \"ShippingTotal\" >= 0 AND \"TaxTotal\" >= 0 AND \"RefundedTotal\" >= 0");
            t.HasCheckConstraint("CK_Orders_TotalIsSum", "\"Total\" = \"Subtotal\" + \"ShippingTotal\" + \"TaxTotal\"");

            // BR-ORD-03: refunds never exceed what was charged.
            t.HasCheckConstraint("CK_Orders_RefundedNotAboveTotal", "\"RefundedTotal\" <= \"Total\"");
            t.HasCheckConstraint("CK_Orders_FailedPaymentAttempts", "\"FailedPaymentAttempts\" >= 0");
        });

        builder.Property(o => o.Version).IsRowVersion();

        // Admin orders list (filter by status, newest first) and the customer's order history.
        builder.HasIndex(o => new { o.Status, o.CreatedUtc });
        builder.HasIndex(o => new { o.CustomerId, o.CreatedUtc });

        // The expiry job (BR-STOCK-01) only ever looks at unpaid orders whose hold has run out.
        builder.HasIndex(o => o.HoldExpiresUtc)
            .HasFilter($"\"Status\" IN ('{nameof(OrderStatus.AwaitingPayment)}', '{nameof(OrderStatus.PaymentProcessing)}')")
            .HasDatabaseName("IX_Orders_Unpaid_HoldExpiresUtc");

        builder.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(o => o.Shipments).WithOne().HasForeignKey(s => s.OrderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(o => o.Payments).WithOne().HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(o => o.Refunds).WithOne().HasForeignKey(r => r.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.HasIndex(l => new { l.OrderId, l.LineNumber }).IsUnique();

        builder.HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(l => l.Source).HasMaxLength(32);
        builder.Property(l => l.SourceProductId).HasMaxLength(64);
        builder.Property(l => l.Name).HasMaxLength(300);
        builder.Property(l => l.PrimaryImageSourceUrl).HasMaxLength(2048);
        builder.Property(l => l.SourceCurrency).HasMaxLength(3);
        builder.Property(l => l.StatusReason).HasMaxLength(500);
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(32);

        builder.Property(l => l.UnitPriceUsd).HasPrecision(12, 2);
        builder.Property(l => l.LineSubtotal).HasPrecision(12, 2);
        builder.Property(l => l.Tax).HasPrecision(12, 2);
        builder.Property(l => l.RefundedAmount).HasPrecision(12, 2);
        builder.Property(l => l.SourceUnitPrice).HasPrecision(12, 2);
        builder.Property(l => l.FxRateToUsd).HasPrecision(18, 8);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_OrderLines_LineNumber", "\"LineNumber\" >= 1");
            t.HasCheckConstraint("CK_OrderLines_Quantity", "\"Quantity\" >= 1");
            t.HasCheckConstraint("CK_OrderLines_AmountsNotNegative",
                "\"UnitPriceUsd\" >= 0 AND \"Tax\" >= 0 AND \"RefundedAmount\" >= 0");

            // BR-MONEY-01: a line is unit price x quantity with no further rounding.
            t.HasCheckConstraint("CK_OrderLines_SubtotalIsUnitTimesQuantity", "\"LineSubtotal\" = \"UnitPriceUsd\" * \"Quantity\"");

            // BR-ORD-03: a line refunds at most its subtotal and its own tax.
            t.HasCheckConstraint("CK_OrderLines_RefundedNotAboveLineTotal", "\"RefundedAmount\" <= \"LineSubtotal\" + \"Tax\"");
            t.HasCheckConstraint("CK_OrderLines_FxRatePositive", "\"FxRateToUsd\" IS NULL OR \"FxRateToUsd\" > 0");
        });

        builder.HasOne(l => l.PartnerOrder).WithOne(p => p.OrderLine)
            .HasForeignKey<PartnerOrder>(p => p.OrderLineId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OrderShipmentConfiguration : IEntityTypeConfiguration<OrderShipment>
{
    public void Configure(EntityTypeBuilder<OrderShipment> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        // BR-SHIP-01: one shipment per seller in an order.
        builder.HasIndex(s => new { s.OrderId, s.Source }).IsUnique();

        builder.Property(s => s.Source).HasMaxLength(32);
        builder.Property(s => s.Method).HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.Fee).HasPrecision(12, 2);
        builder.ToTable(t => t.HasCheckConstraint("CK_OrderShipments_Fee", "\"Fee\" >= 0"));
    }
}

internal sealed class PartnerOrderConfiguration : IEntityTypeConfiguration<PartnerOrder>
{
    public void Configure(EntityTypeBuilder<PartnerOrder> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        // The one-to-one with OrderLine makes OrderLineId unique: one partner order per line.
        builder.Property(p => p.Source).HasMaxLength(32);
        builder.Property(p => p.ExternalRef).HasMaxLength(64);
        builder.HasIndex(p => p.ExternalRef).IsUnique();
        builder.Property(p => p.PartnerOrderId).HasMaxLength(64);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(p => p.Reason).HasMaxLength(500);
        builder.Property(p => p.LastError).HasMaxLength(2000);
        builder.Property(p => p.Version).IsRowVersion();

        builder.ToTable(t => t.HasCheckConstraint("CK_PartnerOrders_Attempts", "\"Attempts\" >= 0"));

        // The partner-order job claims what is due: WHERE Status IN (...) AND NextAttemptUtc <= now.
        builder.HasIndex(p => new { p.Status, p.NextAttemptUtc });
    }
}

internal sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<Refund> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.HasOne<Payment>().WithMany().HasForeignKey(r => r.PaymentId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(r => r.Amount).HasPrecision(12, 2);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.Reason).HasMaxLength(500);
        builder.Property(r => r.LastError).HasMaxLength(2000);
        builder.Property(r => r.Version).IsRowVersion();

        builder.Property(r => r.LineIds)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<List<Guid>>(v, Json) ?? new List<Guid>(),
                new ValueComparer<List<Guid>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (h, id) => HashCode.Combine(h, id)),
                    v => v.ToList()));

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Refunds_AmountPositive", "\"Amount\" > 0");
            t.HasCheckConstraint("CK_Refunds_Attempts", "\"Attempts\" >= 0");
        });

        // The refund job sends Pending refunds and retries Failed ones when due.
        builder.HasIndex(r => new { r.Status, r.NextAttemptUtc });
    }
}
