using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HarborSupply.StandIn.Persistence;

internal static class DatabaseInitializer
{
    /// <summary>Creates the SQLite file and seeds it when it has no items; an existing catalogue is left alone.</summary>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<HarborDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var file = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);

        await db.Database.EnsureCreatedAsync(cancellationToken);
        if (!await db.Items.AnyAsync(cancellationToken))
        {
            db.Items.AddRange(SeedData.Items());
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
