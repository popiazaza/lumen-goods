using HarborSupply.StandIn.Http;
using Microsoft.AspNetCore.Mvc;

namespace HarborSupply.StandIn.Catalogue;

internal static class CatalogueEndpoints
{
    public static IEndpointRouteBuilder MapCatalogue(this IEndpointRouteBuilder app)
    {
        var items = app.MapGroup("/v2/catalog/items");
        items.MapGet("", ListItemsAsync);
        items.MapGet("/{itemCode}", GetItemAsync);
        return app;
    }

    // Query values are bound as strings and parsed by hand, so a bad value gets Harbor's 400 body
    // instead of the framework's binding error.
    private static async Task<IResult> ListItemsAsync(
        [FromQuery] string? limit,
        [FromQuery] string? cursor,
        [FromQuery(Name = "updated_since")] string? updatedSince,
        CatalogueStore store,
        CancellationToken cancellationToken)
    {
        if (!CatalogueQuery.TryParse(limit, cursor, updatedSince, out var query, out var error))
        {
            return HarborErrors.InvalidRequest(error);
        }

        return TypedResults.Json(await store.GetPageAsync(query, cancellationToken), HarborJson.Options);
    }

    private static async Task<IResult> GetItemAsync(string itemCode, CatalogueStore store, CancellationToken cancellationToken)
    {
        var item = await store.FindAsync(itemCode, cancellationToken);
        return item is null
            ? HarborErrors.Result(StatusCodes.Status404NotFound, "ITEM_NOT_FOUND", $"No item with item_code {itemCode}.")
            : TypedResults.Json(ItemResponse.From(item), HarborJson.Options);
    }
}
