using Lumen.Api.Identity;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Lumen.Api.Carts;

/// <summary>
/// The cart API under /api/v1, for guests and customers alike. Every endpoint answers with the whole
/// <see cref="CartDto"/>, so the page and the mini-cart never work anything out themselves. The cart is always the
/// caller's own (session or guest cookie); no endpoint takes a cart id. State-changing calls carry the XSRF header
/// (the /api/v1 group's filter).
/// </summary>
internal static class CartEndpoints
{
    public static RouteGroupBuilder MapCartRoutes(this RouteGroupBuilder api)
    {
        var cart = api.MapGroup("/cart").WithTags("Cart")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .AddEndpointFilter(async (context, next) =>
            {
                // Guests and customers shop; staff accounts (the admin) do not, just as they have no wallet. The role is
                // in the session's claims, the same check the wallet makes.
                var user = context.HttpContext.User;
                return user.Identity?.IsAuthenticated == true && !user.IsInRole(Roles.Customer)
                    ? CartProblems.CustomersOnly().ToResult()
                    : await next(context);
            });

        cart.MapGet("", GetCart).WithName("GetCart");
        cart.MapPost("/lines", AddLine).WithName("AddCartLine")
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        cart.MapPut("/lines/{lineId:guid}", UpdateLine).WithName("UpdateCartLine")
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        cart.MapDelete("/lines/{lineId:guid}", RemoveLine).WithName("RemoveCartLine")
            .ProducesProblem(StatusCodes.Status404NotFound);
        cart.MapPost("/lines/{lineId:guid}/acknowledge", AcknowledgeLine).WithName("AcknowledgeCartLine")
            .ProducesProblem(StatusCodes.Status404NotFound);
        cart.MapPost("/acknowledge", AcknowledgeAll).WithName("AcknowledgeCart");
        return api;
    }

    private static async Task<Ok<CartDto>> GetCart(HttpContext http, CartService carts, CartPresenter presenter, CancellationToken cancellationToken) =>
        TypedResults.Ok(presenter.Present(await carts.FindAsync(http, cancellationToken), http.Request));

    private static async Task<Results<Ok<CartDto>, ProblemHttpResult>> AddLine(
        AddCartLineRequest request, HttpContext http, CartService carts, CartPresenter presenter, CancellationToken cancellationToken) =>
        Answer(await carts.AddAsync(http, request.ProductId!.Value, request.Quantity, cancellationToken), presenter, http);

    private static async Task<Results<Ok<CartDto>, ProblemHttpResult>> UpdateLine(
        Guid lineId, UpdateCartLineRequest request, HttpContext http, CartService carts, CartPresenter presenter, CancellationToken cancellationToken) =>
        Answer(await carts.SetQuantityAsync(http, lineId, request.Quantity, cancellationToken), presenter, http);

    private static async Task<Results<Ok<CartDto>, ProblemHttpResult>> RemoveLine(
        Guid lineId, HttpContext http, CartService carts, CartPresenter presenter, CancellationToken cancellationToken) =>
        Answer(await carts.RemoveAsync(http, lineId, cancellationToken), presenter, http);

    private static async Task<Results<Ok<CartDto>, ProblemHttpResult>> AcknowledgeLine(
        Guid lineId, HttpContext http, CartService carts, CartPresenter presenter, CancellationToken cancellationToken) =>
        Answer(await carts.AcknowledgeAsync(http, lineId, cancellationToken), presenter, http);

    private static async Task<Results<Ok<CartDto>, ProblemHttpResult>> AcknowledgeAll(
        HttpContext http, CartService carts, CartPresenter presenter, CancellationToken cancellationToken) =>
        Answer(await carts.AcknowledgeAsync(http, lineId: null, cancellationToken), presenter, http);

    private static Results<Ok<CartDto>, ProblemHttpResult> Answer(CartEdit edit, CartPresenter presenter, HttpContext http) =>
        edit.Problem is { } problem
            ? problem.ToResult()
            : TypedResults.Ok(presenter.Present(edit.Cart, http.Request));
}
