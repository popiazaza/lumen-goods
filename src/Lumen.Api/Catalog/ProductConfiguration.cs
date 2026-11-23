using System.Text.Json;
using Lumen.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lumen.Api.Catalog;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<Product> builder)
    {
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

        // Own products (and only they) track stock; it can never go negative or be over-reserved. BR-STOCK-01 relies on
        // this as the last line of defence behind the conditional reserve UPDATE.
        builder.Property(p => p.Reserved).HasDefaultValue(0);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Products_OwnStockOnlyForLumen", "(\"Source\" = 'lumen') = (\"OnHand\" IS NOT NULL)");
            t.HasCheckConstraint("CK_Products_StockNotOverReserved",
                "\"Reserved\" >= 0 AND (\"OnHand\" IS NULL OR (\"OnHand\" >= 0 AND \"Reserved\" <= \"OnHand\"))");
        });

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

        // The storefront only ever lists IsListed rows, so the listing indexes are partial; each sort ends with Id, the
        // paging tie-breaker. Trigram indexes serve case-insensitive "contains" search (ILIKE '%term%').
        builder.HasIndex(p => p.Category).HasFilter("\"IsListed\"").HasDatabaseName("IX_Products_Listed_Category");
        builder.HasIndex(p => new { p.PriceUsd, p.Id }).HasFilter("\"IsListed\"").HasDatabaseName("IX_Products_Listed_Price");
        builder.HasIndex(p => new { p.FirstSeenUtc, p.Id }).HasFilter("\"IsListed\"").HasDatabaseName("IX_Products_Listed_Newest");
        builder.HasIndex(p => new { p.Name, p.Id }).HasFilter("\"IsListed\"").HasDatabaseName("IX_Products_Listed_Name");
        builder.HasIndex(p => p.Name).HasMethod("gin").HasOperators("gin_trgm_ops").HasDatabaseName("IX_Products_Name_Trgm");
        builder.HasIndex(p => p.DescriptionText).HasMethod("gin").HasOperators("gin_trgm_ops").HasDatabaseName("IX_Products_DescriptionText_Trgm");

        // BR-CAT-03: search matches the words a customer reads, not markup. A partner description is sanitised HTML
        // (A-21): tags become spaces, the entities the sanitiser writes are decoded (&amp; last, so an encoded "&lt;"
        // stays text), and whitespace runs collapse. Lumen's own descriptions are plain text and are searched as typed.
        builder.Property(p => p.DescriptionText).HasComputedColumnSql(
            $$"""
            CASE WHEN "Source" = '{{ProductSources.Lumen}}' THEN "Description"
            ELSE btrim(regexp_replace(
                replace(replace(replace(replace(regexp_replace("Description", '<[^>]*>', ' ', 'g'),
                    '&lt;', '<'), '&gt;', '>'), '&nbsp;', ' '), '&amp;', '&'),
                '\s+', ' ', 'g'))
            END
            """, stored: true);

        builder.Ignore(p => p.IsSellable);
        builder.Ignore(p => p.AvailableQuantity);
    }
}
