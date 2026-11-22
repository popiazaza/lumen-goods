using System.Globalization;
using Lumen.Api.Infrastructure.Data;
using Lumen.Domain.Catalog;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Lumen.Api.Partners.Media;

/// <summary>
/// <c>GET /media/{productId}/{index}</c>: the image URLs in the product format point here. Own images come from
/// wwwroot/own-media; partner images through <see cref="PartnerImageProxy"/>. Partner URLs are never sent to the browser.
/// </summary>
internal static class MediaEndpoints
{
    public const string OwnMediaPrefix = "own-media/";
    private const string CachedForADay = "public, max-age=86400";

    private static readonly Dictionary<string, string> OwnMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    public static IEndpointRouteBuilder MapMedia(this IEndpointRouteBuilder app)
    {
        app.MapGet("/media/{productId:guid}/{index:int}", GetImage)
            .WithTags("Media")
            .WithSummary("A product image (own or partner), served by Lumen")
            .AllowAnonymous();
        return app;
    }

    private static async Task<Results<FileStreamHttpResult, FileContentHttpResult, ProblemHttpResult>> GetImage(
        Guid productId, int index, LumenDbContext db, PartnerImageProxy proxy, IWebHostEnvironment environment,
        HttpContext http, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking()
            .Where(p => p.Id == productId)
            .Select(p => new { p.Source, p.Images })
            .FirstOrDefaultAsync(cancellationToken);
        if (product is null || index < 0 || index >= product.Images.Count)
        {
            return NotFound(http);
        }

        var sourceUrl = product.Images[index].SourceUrl;
        if (product.Source == ProductSources.Lumen)
        {
            return ServeOwnMedia(sourceUrl, environment, http);
        }

        var image = await proxy.GetAsync(product.Source, sourceUrl, cancellationToken);
        switch (image.Outcome)
        {
            case ImageOutcome.Found:
                SetCacheHeaders(http);
                return TypedResults.File(image.Bytes!, image.ContentType);
            case ImageOutcome.NotFound:
                return NotFound(http);
            case ImageOutcome.Throttled:
                // Lumen's share of the partner's rate limit is spent for now: answer at once rather than hold one of the
                // browser's few connections. The storefront shows a placeholder and the image on a later view.
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers.RetryAfter = RetryAfterSeconds(image.RetryAfter);
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Image not available yet",
                    detail: "Partner images are fetched at the partner's allowed rate. Try again shortly.");
            default:
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "Image not available from the partner right now",
                    detail: "The partner shop did not deliver this image. Try again later.");
        }
    }

    private static Results<FileStreamHttpResult, FileContentHttpResult, ProblemHttpResult> ServeOwnMedia(
        string sourceUrl, IWebHostEnvironment environment, HttpContext http)
    {
        // Own images are files under wwwroot/own-media; nothing else in wwwroot (or outside it) is reachable.
        if (!sourceUrl.StartsWith(OwnMediaPrefix, StringComparison.Ordinal) || sourceUrl.Contains("..", StringComparison.Ordinal)
            || sourceUrl.Contains('\\') || !OwnMediaTypes.TryGetValue(Path.GetExtension(sourceUrl), out var contentType))
        {
            return NotFound(http);
        }

        var file = environment.WebRootFileProvider.GetFileInfo(sourceUrl);
        if (!file.Exists || file.IsDirectory)
        {
            return NotFound(http);
        }

        SetCacheHeaders(http);
        return TypedResults.Stream(file.CreateReadStream(), contentType, lastModified: file.LastModified);
    }

    /// <summary>Whole seconds, rounded up, at least 1 (RFC 9110 delay-seconds).</summary>
    private static string RetryAfterSeconds(TimeSpan? retryAfter) =>
        Math.Max(1, (long)Math.Ceiling((retryAfter ?? TimeSpan.Zero).TotalSeconds)).ToString(CultureInfo.InvariantCulture);

    private static void SetCacheHeaders(HttpContext http)
    {
        http.Response.Headers.CacheControl = CachedForADay;
        http.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
    }

    private static ProblemHttpResult NotFound(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Image not found");
    }
}
