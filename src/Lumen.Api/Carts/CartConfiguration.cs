using Lumen.Api.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lumen.Api.Carts;

internal sealed class CartConfiguration : IEntityTypeConfiguration<Cart>
{
    public void Configure(EntityTypeBuilder<Cart> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.HasOne<AppUser>().WithMany().HasForeignKey(c => c.CustomerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => c.CustomerId).IsUnique();
        builder.HasIndex(c => c.AnonymousTokenHash).IsUnique();

        // Guest-cart clean-up looks for guest carts not touched for 30 days.
        builder.HasIndex(c => c.UpdatedUtc).HasFilter("\"CustomerId\" IS NULL").HasDatabaseName("IX_Carts_Guest_UpdatedUtc");

        builder.ToTable(t =>
        {
            // A cart belongs to a guest or to a customer, never both and never neither.
            t.HasCheckConstraint("CK_Carts_GuestOrCustomer", "(\"CustomerId\" IS NULL) <> (\"AnonymousTokenHash\" IS NULL)");
            t.HasCheckConstraint("CK_Carts_TokenHashIsSha256",
                $"\"AnonymousTokenHash\" IS NULL OR octet_length(\"AnonymousTokenHash\") = {GuestCartToken.HashLength}");
        });

        builder.Property(c => c.Version).IsRowVersion();

        builder.HasMany(c => c.Lines).WithOne().HasForeignKey(l => l.CartId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CartLineConfiguration : IEntityTypeConfiguration<CartLine>
{
    public void Configure(EntityTypeBuilder<CartLine> builder)
    {
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();

        // Products are never deleted (BR-ADM-01), so a line can always show what it was.
        builder.HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(l => new { l.CartId, l.ProductId }).IsUnique();

        builder.Property(l => l.UnitPriceSeenUsd).HasPrecision(12, 2);
        builder.Property(l => l.AvailabilitySeen).HasConversion<string>().HasMaxLength(16);

        builder.ToTable(t =>
        {
            // BR-CART-03: 1 to 10 per line, whatever the code above the database does.
            t.HasCheckConstraint("CK_CartLines_Quantity", $"\"Quantity\" BETWEEN 1 AND {CartLine.MaxQuantity}");
            t.HasCheckConstraint("CK_CartLines_UnitPriceSeen", "\"UnitPriceSeenUsd\" >= 0");
            t.HasCheckConstraint("CK_CartLines_QuantityLimitedFrom", "\"QuantityLimitedFrom\" IS NULL OR \"QuantityLimitedFrom\" > \"Quantity\"");
        });
    }
}
