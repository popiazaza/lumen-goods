using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Lumen.Domain.Catalog;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Catalog;

public sealed partial class CatalogOptions
{
    public const string SectionName = "Catalog";

    /// <summary>BR-CAT-01: a known quantity from 1 up to this value is shown as low stock.</summary>
    [Range(1, 1000)]
    public int LowStockMax { get; init; } = AvailabilityPolicy.DefaultLowStockMax;

    /// <summary>
    /// Base for the absolute image URLs in the product format (e.g. https://shop.example). When empty, the URL the
    /// request arrived on is used, which is right behind the nginx proxy in docker compose.
    /// </summary>
    public string? PublicBaseUrl { get; init; }

    /// <summary>
    /// The shops whose products the catalogue shows, with their display names. Adding a partner is a configuration
    /// change here (plus its adapter), not a code change in the catalogue. No defaults in code: a configuration list
    /// binds on top of a code default instead of replacing it, so the defaults live in appsettings.json.
    /// </summary>
    public List<SellerOptions> Sellers { get; init; } = [];

    internal static bool IsValidSellerList(CatalogOptions options) =>
        options.Sellers.Count > 0
        && options.Sellers.All(s => SellerSourcePattern().IsMatch(s.Source ?? "") && !string.IsNullOrWhiteSpace(s.Name))
        && options.Sellers.Select(s => s.Source).Distinct(StringComparer.Ordinal).Count() == options.Sellers.Count
        && options.Sellers.Any(s => s.Source == ProductSources.Lumen);

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex SellerSourcePattern();
}

public sealed class SellerOptions
{
    /// <summary>The product <c>source</c> value this seller's products carry (e.g. "harbor").</summary>
    public string Source { get; init; } = "";

    /// <summary>Shop name shown to customers (e.g. "Harbor Supply Co.").</summary>
    public string Name { get; init; } = "";
}

internal static class CatalogFeature
{
    public static IServiceCollection AddCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CatalogOptions>()
            .Bind(configuration.GetSection(CatalogOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(CatalogOptions.IsValidSellerList,
                "Catalog:Sellers must list at least the 'lumen' seller; each needs a unique lower-case Source and a Name.")
            .ValidateOnStart();
        services.AddSingleton(sp => new AvailabilityPolicy(sp.GetRequiredService<IOptions<CatalogOptions>>().Value.LowStockMax));
        services.AddSingleton<OwnMediaLibrary>();
        return services;
    }

    /// <summary>Storefront catalogue endpoints under /api/v1.</summary>
    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder api)
    {
        api.MapStorefrontCatalogEndpoints();
        return api;
    }

    /// <summary>The absolute base URL for links the API hands to clients (image URLs).</summary>
    public static string PublicBaseUrl(this HttpRequest request, CatalogOptions options) =>
        string.IsNullOrWhiteSpace(options.PublicBaseUrl)
            ? $"{request.Scheme}://{request.Host}"
            : options.PublicBaseUrl.TrimEnd('/');
}
