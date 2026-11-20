using KestrelOutfitters.StandIn.Data;
using Microsoft.EntityFrameworkCore;

namespace KestrelOutfitters.StandIn.Catalogue;

/// <summary>
/// The partner's own changes to its catalogue and stock ("prices and stock can change at any time").
/// Reached only through the CLI, never through HTTP.
/// </summary>
internal sealed class CatalogueStore(KestrelDb db, TimeProvider time)
{
    public Task<List<Product>> ListAsync(CancellationToken cancellationToken) =>
        db.Products.AsNoTracking().OrderBy(p => p.Ref).ToListAsync(cancellationToken);

    /// <summary>Stock is not part of the feed, so this leaves the feed's Last-Modified alone.</summary>
    public async Task<bool> SetStockAsync(string reference, int units, DateOnly? restockDate, CancellationToken cancellationToken)
    {
        var product = await db.Products.FindAsync([reference], cancellationToken);
        if (product is null)
        {
            return false;
        }

        product.Units = units;
        product.RestockDate = restockDate;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public Task<bool> SetPriceAsync(string reference, decimal priceEur, CancellationToken cancellationToken) =>
        ChangeCatalogueAsync(reference, product => product.PriceEur = priceEur, cancellationToken);

    public Task<bool> SetActiveAsync(string reference, bool active, CancellationToken cancellationToken) =>
        ChangeCatalogueAsync(reference, product => product.Active = active, cancellationToken);

    public Task<DateTimeOffset> GetLastModifiedAsync(CancellationToken cancellationToken) =>
        db.Catalogue.AsNoTracking().Select(c => c.LastModified).SingleAsync(cancellationToken);

    private async Task<bool> ChangeCatalogueAsync(string reference, Action<Product> change, CancellationToken cancellationToken)
    {
        var product = await db.Products.FindAsync([reference], cancellationToken);
        if (product is null)
        {
            return false;
        }

        change(product);
        (await db.Catalogue.SingleAsync(cancellationToken)).RecordChange(time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
