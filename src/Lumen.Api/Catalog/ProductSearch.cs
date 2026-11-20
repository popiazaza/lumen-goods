using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Lumen.Domain.Catalog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lumen.Api.Catalog;

/// <summary>
/// The catalogue query string exactly as received. Every value is a string so a malformed one becomes a 400 with a
/// message naming the parameter, instead of a bare binding failure; <see cref="ProductSearchCriteria.TryParse"/> does
/// the checking.
/// </summary>
public sealed class ProductSearchParameters
{
    [FromQuery(Name = "q"), Description("Case-insensitive text matched literally against name and description.")]
    public string? Q { get; set; }

    [FromQuery(Name = "category"), Description("Repeatable: apparel, footwear, bags, home, outdoor, accessories.")]
    public string[]? Category { get; set; }

    [FromQuery(Name = "seller"), Description("Repeatable seller source, e.g. lumen, harbor, kestrel (see GET /sellers).")]
    public string[]? Seller { get; set; }

    [FromQuery(Name = "minPrice"), Description("Inclusive lower bound in USD. Products without a price are excluded.")]
    public string? MinPrice { get; set; }

    [FromQuery(Name = "maxPrice"), Description("Inclusive upper bound in USD. Products without a price are excluded.")]
    public string? MaxPrice { get; set; }

    [FromQuery(Name = "inStock"), Description("true: only products that can be bought now (sellable, in or low stock).")]
    public string? InStock { get; set; }

    [FromQuery(Name = "sort"), Description("newest (default), price_asc, price_desc or name_asc.")]
    public string? Sort { get; set; }

    [FromQuery(Name = "page"), Description("1-based page number. Default 1.")]
    public string? Page { get; set; }

    [FromQuery(Name = "pageSize"), Description("Items per page, 1 to 96. Default 24.")]
    public string? PageSize { get; set; }
}

public enum ProductSort
{
    Newest,
    PriceAsc,
    PriceDesc,
    NameAsc,
}

/// <summary>A validated catalogue query.</summary>
public sealed record ProductSearchCriteria(
    string? Text,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Sellers,
    decimal? MinPrice,
    decimal? MaxPrice,
    bool InStockOnly,
    ProductSort Sort,
    PageRequest Paging)
{
    public const int MaxTextLength = 200;

    public static readonly IReadOnlyDictionary<string, ProductSort> SortNames = new Dictionary<string, ProductSort>(StringComparer.Ordinal)
    {
        ["newest"] = ProductSort.Newest,
        ["price_asc"] = ProductSort.PriceAsc,
        ["price_desc"] = ProductSort.PriceDesc,
        ["name_asc"] = ProductSort.NameAsc,
    };

    /// <param name="knownSellers">Seller sources from configuration (<see cref="CatalogOptions.Sellers"/>).</param>
    /// <param name="errors">Messages keyed by query parameter name, for a validation ProblemDetails.</param>
    public static bool TryParse(
        ProductSearchParameters input,
        IReadOnlyCollection<string> knownSellers,
        [NotNullWhen(true)] out ProductSearchCriteria? criteria,
        out Dictionary<string, string[]> errors)
    {
        var problems = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Fail(string parameter, string message) => problems[parameter] = [message];

        var text = string.IsNullOrWhiteSpace(input.Q) ? null : input.Q.Trim();
        if (text is { Length: > MaxTextLength })
        {
            Fail("q", $"Search text can be at most {MaxTextLength} characters.");
        }

        var categories = Normalise(input.Category);
        var unknownCategories = categories.Where(c => !ProductCategories.IsValid(c)).ToList();
        if (unknownCategories.Count > 0)
        {
            Fail("category", $"Unknown category '{string.Join("', '", unknownCategories)}'. Use one of: {string.Join(", ", ProductCategories.All)}.");
        }

        var sellers = Normalise(input.Seller);
        var unknownSellers = sellers.Where(s => !knownSellers.Contains(s)).ToList();
        if (unknownSellers.Count > 0)
        {
            Fail("seller", $"Unknown seller '{string.Join("', '", unknownSellers)}'. Use one of: {string.Join(", ", knownSellers)}.");
        }

        var minPrice = ParsePrice(input.MinPrice, "minPrice", Fail);
        var maxPrice = ParsePrice(input.MaxPrice, "maxPrice", Fail);
        if (minPrice > maxPrice)
        {
            Fail("minPrice", "minPrice cannot be greater than maxPrice.");
        }

        var inStockOnly = false;
        if (!string.IsNullOrWhiteSpace(input.InStock) && !bool.TryParse(input.InStock.Trim(), out inStockOnly))
        {
            Fail("inStock", "inStock must be true or false.");
        }

        var sort = ProductSort.Newest;
        if (!string.IsNullOrWhiteSpace(input.Sort) && !SortNames.TryGetValue(input.Sort.Trim().ToLowerInvariant(), out sort))
        {
            Fail("sort", $"Unknown sort '{input.Sort}'. Use one of: {string.Join(", ", SortNames.Keys)}.");
        }

        var paging = PageRequest.Parse(input.Page, input.PageSize, problems);

        errors = problems;
        criteria = problems.Count == 0
            ? new ProductSearchCriteria(text, categories, sellers, minPrice, maxPrice, inStockOnly, sort, paging)
            : null;
        return criteria is not null;
    }

    private static List<string> Normalise(string[]? values) =>
        values is null
            ? []
            : [.. values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal)];

    private static decimal? ParsePrice(string? value, string parameter, Action<string, string> fail)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Invariant culture and no sign, exponent or thousands separators: "49.90" only.
        if (decimal.TryParse(value.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var price))
        {
            return price;
        }

        fail(parameter, $"{parameter} must be a non-negative amount in USD, e.g. 49.90.");
        return null;
    }
}

/// <summary>
/// BR-CAT-03 and BR-CAT-04: what a catalogue listing contains and in which order. Everything runs in SQL, so paging
/// and totals are exact.
/// </summary>
internal static class ProductSearch
{
    public static IQueryable<Product> Filter(this IQueryable<Product> products, ProductSearchCriteria criteria)
    {
        // BR-CAT-03: unlisted products (inactive, deactivated, dropped by a partner, unmapped category) never appear in
        // a listing or search; they still resolve by id.
        var query = products.Where(p => p.IsListed);

        if (criteria.Text is { } text)
        {
            var pattern = LikePattern.Contains(text);
            query = query.Where(p =>
                EF.Functions.ILike(p.Name, pattern, LikePattern.EscapeCharacter)
                || EF.Functions.ILike(p.Description, pattern, LikePattern.EscapeCharacter));
        }

        if (criteria.Categories.Count > 0)
        {
            var categories = criteria.Categories;
            query = query.Where(p => categories.Contains(p.Category));
        }

        if (criteria.Sellers.Count > 0)
        {
            var sellers = criteria.Sellers;
            query = query.Where(p => sellers.Contains(p.Source));
        }

        // BR-CAT-04: the price range is inclusive and compares the USD price; a product without a price has no
        // place in a price range, so any price bound excludes it.
        if (criteria.MinPrice is { } min)
        {
            query = query.Where(p => p.PriceUsd != null && p.PriceUsd >= min);
        }

        if (criteria.MaxPrice is { } max)
        {
            query = query.Where(p => p.PriceUsd != null && p.PriceUsd <= max);
        }

        // BR-CAT-04 "in stock only" (A-22): can be bought right now, so sellable (BR-CAT-02) and in or low stock
        // (BR-CAT-01). The shared expressions keep the filter equal to the badge the product format shows.
        if (criteria.InStockOnly)
        {
            query = query.Where(ProductQueries.IsSellable).Where(ProductQueries.IsInStock);
        }

        return query;
    }

    /// <summary>
    /// BR-CAT-04: products without a price sort last in both price directions, and the id is always the final
    /// tie-breaker so equal sort keys never make an item repeat or vanish between pages.
    /// </summary>
    public static IOrderedQueryable<Product> Sort(this IQueryable<Product> products, ProductSort sort) => sort switch
    {
        ProductSort.PriceAsc => products.OrderBy(p => p.PriceUsd == null).ThenBy(p => p.PriceUsd).ThenBy(p => p.Id),
        ProductSort.PriceDesc => products.OrderBy(p => p.PriceUsd == null).ThenByDescending(p => p.PriceUsd).ThenBy(p => p.Id),
        // Name and newest match the partial listing indexes (Name, Id) and (FirstSeenUtc, Id), the latter read backwards.
        ProductSort.NameAsc => products.OrderBy(p => p.Name).ThenBy(p => p.Id),
        // A-18: "newest" is when Lumen first saw the product, not when it last changed.
        _ => products.OrderByDescending(p => p.FirstSeenUtc).ThenByDescending(p => p.Id),
    };
}
