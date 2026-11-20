using Lumen.Api.Catalog;
using Lumen.Api.Identity;
using Lumen.Api.Infrastructure.Data;
using Lumen.Domain.Catalog;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Lumen.Api.Admin;

/// <summary>
/// Admin page 9, "Own products": list, create, edit, deactivate and reactivate Lumen Goods' own products, including
/// stock and price. Partner products are not editable here (their partner owns them).
/// </summary>
internal static class AdminProductEndpoints
{
    public static RouteGroupBuilder MapAdminProductEndpoints(this RouteGroupBuilder api)
    {
        var products = api.MapGroup("/admin/products").WithTags("Admin: own products").RequireAuthorization(Policies.Admin)
            .ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden);
        products.MapGet("", List).WithName("AdminListOwnProducts");
        products.MapGet("/{id}", Get).WithName("AdminGetOwnProduct").ProducesProblem(StatusCodes.Status404NotFound);
        products.MapPost("", Create).WithName("AdminCreateOwnProduct").ProducesProblem(StatusCodes.Status409Conflict);
        products.MapPut("/{id}", Update).WithName("AdminUpdateOwnProduct")
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        products.MapPost("/{id}/deactivate", Deactivate).WithName("AdminDeactivateOwnProduct").ProducesProblem(StatusCodes.Status404NotFound);
        products.MapPost("/{id}/reactivate", Reactivate).WithName("AdminReactivateOwnProduct").ProducesProblem(StatusCodes.Status404NotFound);

        var media = api.MapGroup("/admin/own-media").WithTags("Admin: own products").RequireAuthorization(Policies.Admin)
            .ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden);
        media.MapGet("", ListMedia).WithName("AdminListOwnMedia");
        media.MapGet("/{file}", GetMediaFile).WithName(OwnMediaFileRoute).ProducesProblem(StatusCodes.Status404NotFound);
        return api;
    }

    private const string OwnMediaFileRoute = "AdminGetOwnMediaFile";

    private static async Task<Results<Ok<PagedResult<AdminProductDto>>, ValidationProblem>> List(
        [AsParameters] AdminProductListParameters parameters, [AsParameters] AdminProductContext context, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var paging = PageRequest.Parse(parameters.Page, parameters.PageSize, errors);

        var status = string.IsNullOrWhiteSpace(parameters.Status) ? "all" : parameters.Status.Trim().ToLowerInvariant();
        if (status is not ("all" or "active" or "inactive"))
        {
            errors["status"] = ["status must be active, inactive or all."];
        }

        var category = string.IsNullOrWhiteSpace(parameters.Category) ? null : parameters.Category.Trim().ToLowerInvariant();
        if (category is not null && !ProductCategories.IsValid(category))
        {
            errors["category"] = [$"category must be one of: {string.Join(", ", ProductCategories.All)}."];
        }

        var text = string.IsNullOrWhiteSpace(parameters.Q) ? null : parameters.Q.Trim();
        if (text is { Length: > ProductSearchCriteria.MaxTextLength })
        {
            errors["q"] = [$"Search text can be at most {ProductSearchCriteria.MaxTextLength} characters."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors, title: "The product list query is not valid");
        }

        var query = context.OwnProducts().AsNoTracking();
        if (status != "all")
        {
            var active = status == "active";
            query = query.Where(p => p.IsActive == active);
        }

        if (category is not null)
        {
            query = query.Where(p => p.Category == category);
        }

        if (text is not null)
        {
            var pattern = LikePattern.Contains(text);
            query = query.Where(p =>
                EF.Functions.ILike(p.Name, pattern, LikePattern.EscapeCharacter)
                || EF.Functions.ILike(p.SourceProductId, pattern, LikePattern.EscapeCharacter));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var page = paging.Skip < totalCount
            ? await query.OrderBy(p => p.Name).ThenBy(p => p.Id).Skip(paging.Skip).Take(paging.PageSize).ToListAsync(cancellationToken)
            : [];

        return TypedResults.Ok(PagedResult<AdminProductDto>.Create([.. page.Select(context.ToDto)], paging, totalCount));
    }

    private static async Task<Results<Ok<AdminProductDto>, ProblemHttpResult>> Get(
        string id, [AsParameters] AdminProductContext context, CancellationToken cancellationToken) =>
        await context.FindAsync(id, tracked: false, cancellationToken) is { } product
            ? TypedResults.Ok(context.ToDto(product))
            : AdminProductContext.NotFound(id);

    private static async Task<Results<Created<AdminProductDto>, ValidationProblem, ProblemHttpResult>> Create(
        CreateOwnProductRequest request, [AsParameters] AdminProductContext context, CancellationToken cancellationToken)
    {
        var errors = OwnProductValidation.Validate(request, context.Media, out var fields);
        var sku = string.IsNullOrWhiteSpace(request.Sku) ? OwnProductValidation.NewSku() : OwnProductValidation.NormaliseSku(request.Sku);
        if (sku is null)
        {
            errors["sku"] = ["SKU must be 2 to 32 letters, digits or dashes, starting with a letter or digit."];
        }

        if (errors.Count > 0 || fields is null || sku is null)
        {
            return TypedResults.ValidationProblem(errors, title: "The product is not valid");
        }

        if (await context.OwnProducts().AnyAsync(p => p.SourceProductId == sku, cancellationToken))
        {
            return SkuTaken(sku);
        }

        var now = context.Time.GetUtcNow();
        var product = new Product
        {
            Source = ProductSources.Lumen,
            SourceProductId = sku,
            Name = fields.Name,
            Category = fields.Category,
            Reserved = 0,
            FirstSeenUtc = now,
        };
        Apply(product, fields, now);
        context.Db.Products.Add(product);

        try
        {
            await context.Db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return SkuTaken(sku);
        }

        return TypedResults.Created($"/api/v1/admin/products/{product.Id}", context.ToDto(product));
    }

    /// <summary>
    /// Replaces the editable fields. BR-ADM-01: the edit must name the version it was based on; if the product changed
    /// since (another admin, or a customer reserving stock), it is rejected instead of silently overwriting that change.
    /// </summary>
    private static async Task<Results<Ok<AdminProductDto>, ValidationProblem, ProblemHttpResult>> Update(
        string id, UpdateOwnProductRequest request, [AsParameters] AdminProductContext context, CancellationToken cancellationToken)
    {
        var errors = OwnProductValidation.Validate(request, context.Media, out var fields);
        if (!AdminProductMapping.TryParseVersion(request.Version, out var version))
        {
            errors["version"] = ["version is required: send back the version from when the product was loaded."];
        }

        if (errors.Count > 0 || fields is null)
        {
            return TypedResults.ValidationProblem(errors, title: "The product is not valid");
        }

        var product = await context.FindAsync(id, tracked: true, cancellationToken);
        if (product is null)
        {
            return AdminProductContext.NotFound(id);
        }

        if (product.Version != version)
        {
            return ChangedBySomeoneElse();
        }

        // BR-ADM-01: units already reserved by orders awaiting payment stay covered by stock on hand, so an edit can
        // never make Lumen oversell what it has promised (the database check constraint is the backstop).
        if (fields.OnHand < product.Reserved)
        {
            return BelowReserved(product.Reserved);
        }

        Apply(product, fields, context.Time.GetUtcNow());
        context.Db.Entry(product).Property(p => p.Version).OriginalValue = version;

        try
        {
            await context.Db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ChangedBySomeoneElse();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation })
        {
            return ChangedBySomeoneElse();
        }

        return TypedResults.Ok(context.ToDto(product));
    }

    private static Task<Results<Ok<AdminProductDto>, ProblemHttpResult>> Deactivate(
        string id, [AsParameters] AdminProductContext context, CancellationToken cancellationToken) =>
        context.SetActiveAsync(id, active: false, cancellationToken);

    private static Task<Results<Ok<AdminProductDto>, ProblemHttpResult>> Reactivate(
        string id, [AsParameters] AdminProductContext context, CancellationToken cancellationToken) =>
        context.SetActiveAsync(id, active: true, cancellationToken);

    private static Ok<IReadOnlyList<OwnMediaFileDto>> ListMedia([AsParameters] AdminProductContext context, LinkGenerator links, HttpContext http) =>
        TypedResults.Ok<IReadOnlyList<OwnMediaFileDto>>([.. context.Media.List().Select(file =>
            new OwnMediaFileDto(file, context.BaseUrl + links.GetPathByName(http, OwnMediaFileRoute, new { file })))]);

    private static Results<FileStreamHttpResult, ProblemHttpResult> GetMediaFile(string file, OwnMediaLibrary media) =>
        media.Find(file) is { } info
            ? TypedResults.Stream(info.CreateReadStream(), "image/png")
            : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Image not found");

    /// <summary>Copies the checked fields onto the entity; the last-updated time moves only if something changed.</summary>
    private static void Apply(Product product, OwnProductFields fields, DateTimeOffset now)
    {
        var changed = product.Name != fields.Name
            || product.Description != fields.Description
            || product.Category != fields.Category
            || product.PriceUsd != fields.Price
            || product.OnHand != fields.OnHand
            || !product.Images.SequenceEqual(fields.Images)
            || product.Attributes.Count != fields.Attributes.Count
            || product.Attributes.Except(fields.Attributes).Any();

        product.Name = fields.Name;
        product.Description = fields.Description;
        product.Category = fields.Category;
        product.PriceUsd = fields.Price;
        product.SourcePrice = fields.Price;
        product.SourceCurrency = "USD";
        product.OnHand = fields.OnHand;
        product.Images = fields.Images;
        product.Attributes = fields.Attributes;
        if (changed || product.LastUpdatedUtc == default)
        {
            product.LastUpdatedUtc = now;
        }
    }

    private static ProblemHttpResult ChangedBySomeoneElse() => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Changed by someone else",
        detail: "This product was changed by someone else since you loaded it (another admin, or a customer reserving stock). "
            + "Reload it, then apply your changes again.");

    private static ProblemHttpResult BelowReserved(int reserved) => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "On hand cannot go below reserved stock",
        detail: $"{reserved} unit(s) are reserved by orders awaiting payment, so on hand must be at least {reserved}.",
        extensions: new Dictionary<string, object?> { ["reserved"] = reserved });

    private static ProblemHttpResult SkuTaken(string sku) => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "SKU already in use",
        detail: $"Another own product already has SKU '{sku}'.");

}

/// <summary>The per-request services every admin product endpoint needs, bound with [AsParameters].</summary>
internal sealed record AdminProductContext(
    LumenDbContext Db,
    AvailabilityPolicy Policy,
    IOptions<CatalogOptions> Options,
    OwnMediaLibrary Media,
    TimeProvider Time,
    HttpRequest Request)
{
    public string BaseUrl => Request.PublicBaseUrl(Options.Value);

    public IQueryable<Product> OwnProducts() => Db.Products.Where(p => p.Source == ProductSources.Lumen);

    public AdminProductDto ToDto(Product product) => product.ToAdminDto(Policy, BaseUrl);

    public async Task<Product?> FindAsync(string id, bool tracked, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var productId))
        {
            return null;
        }

        var query = tracked ? OwnProducts() : OwnProducts().AsNoTracking();
        return await query.FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);
    }

    /// <summary>
    /// BR-ADM-01: own products are never deleted (orders reference them). Deactivating hides the product from the
    /// catalogue and makes it unsellable; it still resolves by id. Idempotent, and a single UPDATE of the status
    /// columns, so it cannot overwrite a stock reservation made at the same moment.
    /// </summary>
    public async Task<Results<Ok<AdminProductDto>, ProblemHttpResult>> SetActiveAsync(string id, bool active, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var productId))
        {
            return NotFound(id);
        }

        var now = Time.GetUtcNow();
        await OwnProducts()
            .Where(p => p.Id == productId && p.IsActive != active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.IsActive, active)
                .SetProperty(p => p.IsListed, active)
                .SetProperty(p => p.LastUpdatedUtc, now), cancellationToken);

        return await FindAsync(id, tracked: false, cancellationToken) is { } product
            ? TypedResults.Ok(ToDto(product))
            : NotFound(id);
    }

    public static ProblemHttpResult NotFound(string id) => TypedResults.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Product not found",
        detail: $"There is no Lumen Goods product with id '{id}'.");
}
