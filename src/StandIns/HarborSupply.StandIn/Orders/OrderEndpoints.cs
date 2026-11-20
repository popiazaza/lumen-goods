using System.Diagnostics;
using System.Text.Json;
using HarborSupply.StandIn.Http;

namespace HarborSupply.StandIn.Orders;

internal static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrders(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v2/orders", PlaceOrderAsync);
        return app;
    }

    // The body is read by hand rather than bound, so malformed JSON or a wrong type ("qty": "2", "qty": 1.5)
    // gets Harbor's 400 body instead of the framework's.
    private static async Task<IResult> PlaceOrderAsync(HttpRequest request, OrderService orders, CancellationToken cancellationToken)
    {
        OrderRequest? order;
        try
        {
            order = await JsonSerializer.DeserializeAsync<OrderRequest>(request.Body, HarborJson.Options, cancellationToken);
        }
        catch (JsonException)
        {
            return HarborErrors.InvalidRequest("The body must be a JSON order: external_ref, lines and ship_to.");
        }

        return order is null
            ? HarborErrors.InvalidRequest("The body must be a JSON order: external_ref, lines and ship_to.")
            : ToHttpResult(await orders.PlaceAsync(order, cancellationToken));
    }

    private static IResult ToHttpResult(OrderOutcome outcome) => outcome switch
    {
        OrderOutcome.Accepted a => TypedResults.Json(new OrderResponse(a.OrderId, a.Status), HarborJson.Options, statusCode: StatusCodes.Status201Created),
        OrderOutcome.Replayed r => TypedResults.Json(new OrderResponse(r.OrderId, r.Status), HarborJson.Options),
        OrderOutcome.Invalid i => HarborErrors.InvalidRequest(i.Message),
        OrderOutcome.InvalidItem i => HarborErrors.Result(StatusCodes.Status422UnprocessableEntity, "INVALID_ITEM", $"Item {i.ItemCode} {i.Reason}."),
        OrderOutcome.InsufficientStock s => HarborErrors.InsufficientStock(s.ItemCode, s.Available),
        _ => throw new UnreachableException(),
    };
}
