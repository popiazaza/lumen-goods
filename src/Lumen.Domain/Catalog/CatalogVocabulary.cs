namespace Lumen.Domain.Catalog;

/// <summary>Where a product comes from. Values are the storefront format's <c>source</c> strings.</summary>
public static class ProductSources
{
    public const string Lumen = "lumen";
    public const string Harbor = "harbor";
    public const string Kestrel = "kestrel";

    public static readonly IReadOnlyList<string> All = [Lumen, Harbor, Kestrel];
}

/// <summary>The storefront format's categories. Partner categories are mapped onto these by configuration.</summary>
public static class ProductCategories
{
    public const string Apparel = "apparel";
    public const string Footwear = "footwear";
    public const string Bags = "bags";
    public const string Home = "home";
    public const string Outdoor = "outdoor";
    public const string Accessories = "accessories";

    public static readonly IReadOnlyList<string> All = [Apparel, Footwear, Bags, Home, Outdoor, Accessories];

    public static bool IsValid(string? category) => category is not null && All.Contains(category);
}
