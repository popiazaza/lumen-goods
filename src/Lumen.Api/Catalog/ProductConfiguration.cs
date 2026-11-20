using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lumen.Api.Catalog;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.Source).HasMaxLength(32);
        builder.Property(p => p.SourceProductId).HasMaxLength(64);
        builder.HasIndex(p => new { p.Source, p.SourceProductId }).IsUnique();

        builder.Property(p => p.Name).HasMaxLength(300);
        builder.Property(p => p.Category).HasMaxLength(32);
        builder.Property(p => p.SourceCurrency).HasMaxLength(3);
        builder.Property(p => p.PriceUsd).HasPrecision(12, 2);
        builder.Property(p => p.SourcePrice).HasPrecision(12, 2);
        builder.Property(p => p.FxRateToUsd).HasPrecision(18, 8);
        builder.Property(p => p.StatusWhenQuantityUnknown).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.ContentHash).HasMaxLength(64);

        // Own stock can never go negative or be over-reserved (BR-STOCK-01 relies on this as the last line of defence).
        builder.ToTable(t => t.HasCheckConstraint("ck_products_own_stock",
            "\"OnHand\" IS NULL OR (\"OnHand\" >= 0 AND \"Reserved\" >= 0 AND \"Reserved\" <= \"OnHand\")"));

        builder.Property(p => p.Version).IsRowVersion();

        builder.Property(p => p.Images)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<List<ProductImage>>(v, Json) ?? new List<ProductImage>(),
                new ValueComparer<List<ProductImage>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (h, i) => HashCode.Combine(h, i.GetHashCode())),
                    v => v.ToList()));

        builder.Property(p => p.Attributes)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, Json) ?? new Dictionary<string, string>(),
                new ValueComparer<Dictionary<string, string>>(
                    (a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
                    v => v.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
                    v => new Dictionary<string, string>(v)));

        builder.HasIndex(p => new { p.IsListed, p.Category });
        builder.HasIndex(p => p.PriceUsd);
        builder.HasIndex(p => p.FirstSeenUtc);

        builder.Ignore(p => p.IsSellable);
        builder.Ignore(p => p.AvailableQuantity);
    }
}
