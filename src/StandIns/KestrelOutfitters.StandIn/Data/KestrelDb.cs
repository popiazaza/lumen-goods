using KestrelOutfitters.StandIn.Catalogue;
using KestrelOutfitters.StandIn.Orders;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace KestrelOutfitters.StandIn.Data;

/// <summary>Kestrel's own data, in its own SQLite file. Shared with nothing on the Lumen side.</summary>
internal sealed class KestrelDb(DbContextOptions<KestrelDb> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    public DbSet<CatalogueState> Catalogue => Set<CatalogueState>();

    public DbSet<PurchaseOrder> Orders => Set<PurchaseOrder>();

    public static void Configure(DbContextOptionsBuilder builder, string databasePath) =>
        builder.UseSqlite(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());

    /// <summary>For the CLI, which runs without the web host.</summary>
    public static KestrelDb Open(string databasePath)
    {
        var builder = new DbContextOptionsBuilder<KestrelDb>();
        Configure(builder, databasePath);
        return new KestrelDb(builder.Options);
    }

    // SQLite has no native timestamp type; Unix milliseconds keep comparisons and ORDER BY in SQL.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UnixMillisecondsConverter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(product =>
        {
            product.HasKey(p => p.Ref);
            product.OwnsMany(p => p.Specs, spec => spec.ToJson());
        });

        modelBuilder.Entity<CatalogueState>().HasKey(c => c.Id);

        modelBuilder.Entity<PurchaseOrder>(order =>
        {
            order.HasIndex(o => o.Ticket).IsUnique();
            order.HasIndex(o => o.Reference).IsUnique();
            order.HasIndex(o => new { o.State, o.DecideAt });
            order.Property(o => o.State).HasConversion<string>();
            order.OwnsMany(o => o.Lines, line => line.ToJson());
            order.OwnsOne(o => o.Delivery);
        });
    }

    private sealed class UnixMillisecondsConverter() : ValueConverter<DateTimeOffset, long>(
        value => value.ToUnixTimeMilliseconds(),
        value => DateTimeOffset.FromUnixTimeMilliseconds(value));
}
