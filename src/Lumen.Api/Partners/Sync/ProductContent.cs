using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lumen.Api.Catalog;
using Lumen.Domain.Catalog;
using Lumen.Domain.Money;

namespace Lumen.Api.Partners.Sync;

/// <summary>
/// Everything a partner sync writes to a product row, exactly as it will be stored. Its hash is the product's
/// <see cref="Product.ContentHash"/>: it covers the stored result, not the partner's raw data, so anything that changes
/// what is stored (a partner edit, the EUR rate, the category map, attribute synonyms, a stock read) changes the hash,
/// and nothing else does. A sync writes a row only when the hash differs.
/// </summary>
internal sealed record ProductContent(
    string Name,
    string Description,
    string Category,
    decimal? PriceUsd,
    decimal? SourcePrice,
    string SourceCurrency,
    decimal? FxRateToUsd,
    int? Quantity,
    AvailabilityStatus StatusWhenQuantityUnknown,
    bool IsActive,
    bool IsListed,
    IReadOnlyList<ProductImage> Images,
    IReadOnlyDictionary<string, string> Attributes)
{
    /// <summary>Bump when the hashed fields change, so every product is rewritten once under the new definition.</summary>
    private const string HashVersion = "partner-content-v1";

    public const int MaxNameLength = 300;

    public static ProductContent From(Product product) => new(
        product.Name, product.Description, product.Category, product.PriceUsd, product.SourcePrice, product.SourceCurrency,
        product.FxRateToUsd, product.Quantity, product.StatusWhenQuantityUnknown, product.IsActive, product.IsListed,
        [.. product.Images], new Dictionary<string, string>(product.Attributes));

    public void ApplyTo(Product product)
    {
        product.Name = Name;
        product.Description = Description;
        product.Category = Category;
        product.PriceUsd = PriceUsd;
        product.SourcePrice = SourcePrice;
        product.SourceCurrency = SourceCurrency;
        product.FxRateToUsd = FxRateToUsd;
        product.Quantity = Quantity;
        product.StatusWhenQuantityUnknown = StatusWhenQuantityUnknown;
        product.IsActive = IsActive;
        product.IsListed = IsListed;
        product.Images = [.. Images];
        product.Attributes = new Dictionary<string, string>(Attributes);

        // Partner stock lives in Quantity; OnHand/Reserved are Lumen's own stock (a check constraint enforces this).
        product.OnHand = null;
        product.Reserved = 0;
        product.ContentHash = ComputeHash();
    }

    /// <summary>SHA-256 over a length-prefixed, culture-free rendering of every field (64 hex characters).</summary>
    public string ComputeHash()
    {
        var text = new StringBuilder(HashVersion);
        Add(text, Name);
        Add(text, Description);
        Add(text, Category);
        Add(text, Number(PriceUsd, 2));
        Add(text, Number(SourcePrice, 2));
        Add(text, SourceCurrency);
        Add(text, Number(FxRateToUsd, 8));
        Add(text, Quantity?.ToString(CultureInfo.InvariantCulture));
        Add(text, StatusWhenQuantityUnknown.ToString());
        Add(text, IsActive ? "active" : "inactive");
        Add(text, IsListed ? "listed" : "unlisted");
        foreach (var image in Images)
        {
            Add(text, image.SourceUrl);
            Add(text, image.IsPrimary ? "primary" : "secondary");
        }

        Add(text, "attributes");
        foreach (var (key, value) in Attributes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Add(text, key);
            Add(text, value);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static void Add(StringBuilder text, string? value)
    {
        if (value is null)
        {
            text.Append("|-");
            return;
        }

        text.Append('|').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }

    /// <summary>Rounded to the column's scale and trimmed of trailing zeros, so 49.9 and 49.90 (as read back) hash alike.</summary>
    private static string? Number(decimal? value, int decimals) =>
        value is { } v
            ? (decimals == 2 ? MoneyMath.RoundToCents(v) : Math.Round(v, decimals, MidpointRounding.AwayFromZero))
                .ToString("0.##########", CultureInfo.InvariantCulture)
            : null;
}
