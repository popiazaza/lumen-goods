using Lumen.Api.Infrastructure.Data;
using Lumen.Domain.Catalog;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Catalog;

/// <summary>A shop whose products the catalogue shows; the storefront labels partner products with <see cref="Name"/>.</summary>
public sealed record SellerDto(string Source, string Name);

/// <summary>
/// The storefront's read side: everything is served from Lumen's own products table, never from a live partner call,
/// so browsing keeps working when a partner is slow or down.
/// </summary>
internal static class StorefrontCatalogEndpoints
{
    public static RouteGroupBuilder MapStorefrontCatalogEndpoints(this RouteGroupBuilder api)
    {
        var products = api.MapGroup("/products").WithTags("Catalog");
        products.MapGet("", ListProducts).WithName("ListProducts");
        products.MapGet("/{id}", GetProduct).WithName("GetProduct").ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/sellers", ListSellers).WithTags("Catalog").WithName("ListSellers");
        return api;
    }

    private static async Task<Results<Ok<PagedResult<ProductDto>>, ValidationProblem>> ListProducts(
        [AsParameters] ProductSearchParameters parameters,
        LumenDbContext db,
        AvailabilityPolicy policy,
        IOptions<CatalogOptions> options,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var sellers = options.Value.Sellers.Select(s => s.Source).ToList();
        if (!ProductSearchCriteria.TryParse(parameters, sellers, out var criteria, out var errors))
        {
            return TypedResults.ValidationProblem(errors, title: "The catalogue query is not valid");
        }

        var query = db.Products.AsNoTracking().Filter(criteria);
        var totalCount = await query.CountAsync(cancellationToken);
        var paging = criteria.Paging;
        var page = paging.Skip < totalCount
            ? await query.Sort(criteria.Sort).Skip(paging.Skip).Take(paging.PageSize).ToListAsync(cancellationToken)
            : [];

        var baseUrl = request.PublicBaseUrl(options.Value);
        return TypedResults.Ok(PagedResult<ProductDto>.Create(
            [.. page.Select(p => p.ToDto(policy, baseUrl))], paging, totalCount));
    }

    /// <summary>BR-CAT-02: resolves unlisted products too, so carts, bookmarks and order history keep working.</summary>
    private static async Task<Results<Ok<ProductDto>, ProblemHttpResult>> GetProduct(
        string id,
        LumenDbContext db,
        AvailabilityPolicy policy,
        IOptions<CatalogOptions> options,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var product = Guid.TryParse(id, out var productId)
            ? await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId, cancellationToken)
            : null;

        return product is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Product not found",
                detail: $"There is no product with id '{id}'.")
            : TypedResults.Ok(product.ToDto(policy, request.PublicBaseUrl(options.Value)));
    }

    private static Ok<IReadOnlyList<SellerDto>> ListSellers(IOptions<CatalogOptions> options) =>
        TypedResults.Ok<IReadOnlyList<SellerDto>>([.. options.Value.Sellers.Select(s => new SellerDto(s.Source, s.Name))]);
}
