using KestrelOutfitters.StandIn.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KestrelOutfitters.StandIn.Catalogue;

/// <summary>Contract: GET /feed/products.xml. Whole catalogue, no paging, no stock; slow and sometimes failing.</summary>
internal static class FeedEndpoint
{
    // The sample renders Kestrel's local time with an explicit offset. A fixed +01:00 matches it and keeps
    // Lumen honest about converting offsets instead of assuming UTC.
    private static readonly TimeSpan KestrelUtcOffset = TimeSpan.FromHours(1);

    public static void MapFeed(this IEndpointRouteBuilder app) => app.MapGet("/feed/products.xml", GetFeedAsync);

    private static async Task<IResult> GetFeedAsync(
        HttpContext context,
        IDbContextFactory<KestrelDb> dbFactory,
        ContractRandom random,
        TimeProvider time,
        IOptions<KestrelOutfittersOptions> options)
    {
        var settings = options.Value;
        var cancellationToken = context.RequestAborted;

        // Contract quirks: 2-4 s and about 1 in 10 failures, for every authenticated feed request (304s too).
        await Task.Delay(random.Between(settings.FeedLatencyMin, settings.FeedLatencyMax), time, cancellationToken);
        if (random.Chance(settings.FeedFailureRate))
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var store = new CatalogueStore(db, time);
        var lastModified = await store.GetLastModifiedAsync(cancellationToken);
        context.Response.GetTypedHeaders().LastModified = lastModified;

        if (context.Request.GetTypedHeaders().IfModifiedSince is { } since && since >= lastModified)
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var products = await store.ListAsync(cancellationToken);
        // "generated" is when this catalogue version came into being, so it moves with Last-Modified
        // and an unchanged catalogue yields a byte-identical document.
        var document = CatalogueXml.Write(products, lastModified.ToOffset(KestrelUtcOffset), PictureBaseUrl(context, settings));
        return Results.Bytes(document, "application/xml; charset=utf-8");
    }

    private static string PictureBaseUrl(HttpContext context, KestrelOutfittersOptions settings) =>
        string.IsNullOrEmpty(settings.PublicBaseUrl)
            ? $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}"
            : settings.PublicBaseUrl.TrimEnd('/');
}
