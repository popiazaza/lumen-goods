using Lumen.Api.Catalog;
using Lumen.Api.Partners.Mapping;
using Lumen.Api.Partners.Ports;

namespace Lumen.Api.Partners.Sync;

/// <summary>Turns a partner snapshot into the content stored for it, given what is stored now (if anything).</summary>
internal sealed class SnapshotMapper(CurrencyConverter currency)
{
    /// <returns>
    /// The content to store, or null when the product should not be stored at all (a product Lumen has never stored,
    /// in a category that is not mapped: it is not part of the shop). Problems found are added to <paramref name="problems"/>.
    /// </returns>
    public ProductContent? ToContent(PartnerProductSnapshot snapshot, Product? existing, List<string> problems)
    {
        problems.AddRange(snapshot.Problems);

        // An unmapped category hides the product. A stored product keeps its last category, so carts and order
        // history that point at it still resolve; it just leaves the listing.
        var category = snapshot.Category ?? existing?.Category;
        if (category is null)
        {
            return null;
        }

        var price = currency.ToUsd(snapshot.Price, snapshot.Currency);
        if (price.Problem is { } priceProblem)
        {
            problems.Add($"{snapshot.SourceProductId}: {priceProblem}; shown as not sellable.");
        }

        // A catalogue without stock (Kestrel) leaves stock as the stock refresh last wrote it; new products start
        // unknown, which shows as out of stock, never in stock without evidence.
        var stock = snapshot.Stock
            ?? (existing is null ? StockLevel.Unknown : new StockLevel(existing.Quantity, existing.StatusWhenQuantityUnknown));

        var name = snapshot.Name.Length > ProductContent.MaxNameLength ? snapshot.Name[..ProductContent.MaxNameLength] : snapshot.Name;

        return new ProductContent(
            Name: name,
            Description: snapshot.Description,
            Category: category,
            PriceUsd: price.Usd,
            SourcePrice: price.SourcePrice,
            SourceCurrency: price.SourceCurrency,
            FxRateToUsd: price.RateToUsd,
            Quantity: stock.Quantity,
            StatusWhenQuantityUnknown: stock.StatusWhenQuantityUnknown,
            IsActive: snapshot.IsActive,
            IsListed: snapshot.IsActive && snapshot.Category is not null,
            Images: snapshot.Images,
            Attributes: snapshot.Attributes);
    }
}
