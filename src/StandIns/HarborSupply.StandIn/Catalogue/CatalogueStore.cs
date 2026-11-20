using HarborSupply.StandIn.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HarborSupply.StandIn.Catalogue;

/// <summary>Read side of the catalogue: keyset paging and single-item lookup.</summary>
internal sealed class CatalogueStore(IDbContextFactory<HarborDbContext> dbFactory)
{
    public async Task<ItemPageResponse> GetPageAsync(CatalogueQuery query, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var items = db.Items.AsNoTracking();

        // Strictly after, as worded ("Returns only items changed after it"), against the full-precision stored time.
        if (query.UpdatedSince is { } since)
        {
            items = items.Where(i => i.UpdatedAt > since);
        }

        if (query.After is { } after)
        {
            var (afterTime, afterCode) = (after.UpdatedAt, after.ItemCode);
            items = items.Where(i => i.UpdatedAt > afterTime || (i.UpdatedAt == afterTime && string.Compare(i.ItemCode, afterCode) > 0));
        }

        // One extra row tells us whether another page exists without a COUNT query.
        var rows = await items.OrderBy(i => i.UpdatedAt).ThenBy(i => i.ItemCode).Take(query.Limit + 1).ToListAsync(cancellationToken);
        var page = rows.Take(query.Limit).ToList();
        var nextCursor = rows.Count > query.Limit ? new PageCursor(page[^1].UpdatedAt, page[^1].ItemCode).Encode() : null;

        return new ItemPageResponse([.. page.Select(ItemResponse.From)], nextCursor);
    }

    public async Task<CatalogItem?> FindAsync(string itemCode, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.ItemCode == itemCode, cancellationToken);
    }
}
