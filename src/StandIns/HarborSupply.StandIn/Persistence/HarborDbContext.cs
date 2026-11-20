using Microsoft.EntityFrameworkCore;

namespace HarborSupply.StandIn.Persistence;

internal sealed class HarborDbContext(DbContextOptions<HarborDbContext> options) : DbContext(options)
{
    public DbSet<CatalogItem> Items => Set<CatalogItem>();

    public DbSet<HarborOrder> Orders => Set<HarborOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CatalogItem>(item =>
        {
            item.ToTable("Items");
            item.HasKey(i => i.ItemCode);

            // SQLite has no UTC type; mark values read back as UTC so they format with "Z". EF stores the full
            // precision as text (yyyy-MM-dd HH:mm:ss.FFFFFFF), which compares and sorts in time order.
            item.Property(i => i.UpdatedAt).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

            // The paging order: keyset over (updated_at, item_code).
            item.HasIndex(i => new { i.UpdatedAt, i.ItemCode });
            item.OwnsMany(i => i.Props, props => props.ToJson());
        });

        modelBuilder.Entity<HarborOrder>(order =>
        {
            order.ToTable("Orders");
            order.HasKey(o => o.Number);
            order.Property(o => o.Number).ValueGeneratedNever();
            order.HasIndex(o => o.ExternalRef).IsUnique();
        });
    }
}
