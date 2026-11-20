using KestrelOutfitters.StandIn.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace KestrelOutfitters.StandIn.Data;

internal static class Database
{
    /// <summary>Creates the SQLite file and seeds the catalogue on first run; an existing catalogue is kept.</summary>
    public static async Task PrepareAsync(KestrelDb db, string databasePath, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await db.Database.EnsureCreatedAsync(cancellationToken);
        if (await db.Products.AnyAsync(cancellationToken))
        {
            return;
        }

        db.Products.AddRange(SeedCatalogue.Products());
        db.Catalogue.Add(new CatalogueState { LastModified = CatalogueState.ToHttpDatePrecision(now) });
        await db.SaveChangesAsync(cancellationToken);
    }
}
