using HarborSupply.StandIn.Catalogue;
using HarborSupply.StandIn.Http;

namespace HarborSupply.StandIn.Media;

internal static class MediaEndpoints
{
    /// <summary>
    /// Serves the files that media[].href points at. The hrefs are absolute paths (/media/...), so this route sits
    /// at the root, not under /v2. Like every request it needs the key and counts toward the rate limit.
    /// </summary>
    public static IEndpointRouteBuilder MapMedia(this IEndpointRouteBuilder app)
    {
        app.MapGet("/media/{itemCode}/{fileName}", GetImageAsync);
        return app;
    }

    private static async Task<IResult> GetImageAsync(
        string itemCode, string fileName, CatalogueStore store, ProductImageRenderer renderer, CancellationToken cancellationToken)
    {
        var item = await store.FindAsync(itemCode, cancellationToken);
        var view = item?.ImageViews.FirstOrDefault(v => fileName == v + MediaPaths.Extension);
        if (item is null || view is null)
        {
            return HarborErrors.Result(StatusCodes.Status404NotFound, "NOT_FOUND", "No image exists at this path.");
        }

        return TypedResults.Bytes(renderer.Render(item.ItemCode, item.Dept, view), "image/png");
    }
}
