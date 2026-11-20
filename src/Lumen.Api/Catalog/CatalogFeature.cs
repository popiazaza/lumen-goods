using System.ComponentModel.DataAnnotations;
using Lumen.Domain.Catalog;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Catalog;

public sealed class CatalogOptions
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
}

internal static class CatalogFeature
{
    public static IServiceCollection AddCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CatalogOptions>()
            .Bind(configuration.GetSection(CatalogOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton(sp => new AvailabilityPolicy(sp.GetRequiredService<IOptions<CatalogOptions>>().Value.LowStockMax));
        return services;
    }

    /// <summary>Storefront catalogue endpoints under /api/v1.</summary>
    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder api) => api;

    /// <summary>The absolute base URL for links the API hands to clients (image URLs).</summary>
    public static string PublicBaseUrl(this HttpRequest request, CatalogOptions options) =>
        string.IsNullOrWhiteSpace(options.PublicBaseUrl)
            ? $"{request.Scheme}://{request.Host}"
            : options.PublicBaseUrl.TrimEnd('/');
}
