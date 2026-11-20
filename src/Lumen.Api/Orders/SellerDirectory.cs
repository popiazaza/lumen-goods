using Lumen.Api.Catalog;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Orders;

/// <summary>
/// The shops' display names and their order on the order pages, from <c>Catalog:Sellers</c> (the same names the
/// catalogue shows): Lumen Goods first, then the partners as configured.
/// </summary>
internal sealed class SellerDirectory(IOptionsMonitor<CatalogOptions> catalog)
{
    /// <summary>The shop's name, e.g. "Kestrel Outfitters"; the source itself when it is not configured.</summary>
    public string Name(string source) =>
        catalog.CurrentValue.Sellers.FirstOrDefault(s => string.Equals(s.Source, source, StringComparison.Ordinal))?.Name ?? source;

    /// <summary>Position on the order pages; unknown sellers go last.</summary>
    public int Rank(string source)
    {
        var index = catalog.CurrentValue.Sellers.FindIndex(s => string.Equals(s.Source, source, StringComparison.Ordinal));
        return index < 0 ? int.MaxValue : index;
    }

    public IReadOnlyList<string> Names(IEnumerable<string> sources) =>
        [.. sources.Distinct(StringComparer.Ordinal).OrderBy(Rank).ThenBy(s => s, StringComparer.Ordinal).Select(Name)];
}
