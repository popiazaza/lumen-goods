using System.Collections.Concurrent;
using KestrelOutfitters.StandIn.Data;
using Microsoft.EntityFrameworkCore;

namespace KestrelOutfitters.StandIn.Catalogue;

/// <summary>Serves the picture URLs the feed points at. Behind Basic auth like every other Kestrel request.</summary>
internal static class ImageEndpoint
{
    // Pictures are a pure function of (ref, group, size), so rendering once per process is enough.
    private static readonly ConcurrentDictionary<(string Ref, string Group, PictureSize Size), byte[]> Rendered = new();

    public static void MapImages(this IEndpointRouteBuilder app) => app.MapGet("/img/{file}", GetImageAsync);

    private static async Task<IResult> GetImageAsync(string file, IDbContextFactory<KestrelDb> dbFactory, CancellationToken cancellationToken)
    {
        if (!ProductPictures.TryParseFileName(file, out var productRef, out var size))
        {
            return Results.StatusCode(StatusCodes.Status404NotFound);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var product = await db.Products.AsNoTracking()
            .Where(p => p.Ref == productRef)
            .Select(p => new { p.Ref, p.Group, p.HasThumb, p.HasLarge })
            .SingleOrDefaultAsync(cancellationToken);
        var published = size == PictureSize.Thumb ? product?.HasThumb : product?.HasLarge;
        if (product is null || published != true)
        {
            return Results.StatusCode(StatusCodes.Status404NotFound);
        }

        var png = Rendered.GetOrAdd((product.Ref, product.Group, size), key => ProductPictures.Render(key.Ref, key.Group, key.Size));
        return Results.Bytes(png, "image/png");
    }
}
