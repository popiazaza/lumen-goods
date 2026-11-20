using Lumen.Api.Carts;
using Lumen.Api.Catalog;
using Lumen.Domain.Pricing;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Checkout;

/// <summary>Problem types the checkout endpoints return besides <see cref="PriceChangedProblem"/>.</summary>
public static class CheckoutProblemTypes
{
    /// <summary>422: Place order with an empty cart.</summary>
    public const string EmptyCart = "urn:lumen:problem:cart-empty";

    /// <summary>422: Express asked for a cart without Lumen's own items (BR-SHIP-01).</summary>
    public const string ShippingMethodNotAvailable = "urn:lumen:problem:shipping-method-not-available";

    /// <summary>409: the checkout key is already used by another customer's order.</summary>
    public const string CheckoutKeyTaken = "urn:lumen:problem:checkout-key-taken";
}

internal static class CheckoutEndpoints
{
    public static RouteGroupBuilder MapCheckout(this RouteGroupBuilder api)
    {
        var checkout = api.MapGroup("/checkout").WithTags("Checkout").RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
        checkout.MapGet("/preview", Preview).WithName("CheckoutPreview")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        checkout.MapPost("/orders", PlaceOrder).WithName("PlaceOrder")
            .Produces<PriceChangedProblem>(StatusCodes.Status409Conflict, "application/problem+json")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return api;
    }

    private static async Task<Results<Ok<CheckoutPreviewDto>, ValidationProblem, ProblemHttpResult, UnauthorizedHttpResult>> Preview(
        string? shippingMethod,
        HttpContext http,
        CheckoutPreviewService previews,
        IOptions<CatalogOptions> catalog,
        CancellationToken cancellationToken)
    {
        if (http.User.CustomerId() is not { } customerId)
        {
            return TypedResults.Unauthorized();
        }

        if (!TryParseShippingMethod(shippingMethod, out var method))
        {
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]> { ["shippingMethod"] = ["shippingMethod must be standard or express."] },
                title: "The preview query is not valid");
        }

        try
        {
            return TypedResults.Ok(await previews.PreviewAsync(customerId, method, http.Request.PublicBaseUrl(catalog.Value), cancellationToken));
        }
        catch (ShippingMethodNotAvailableException ex)
        {
            return ShippingNotAvailable(ex.Message);
        }
    }

    private static async Task<IResult> PlaceOrder(
        PlaceOrderRequest request,
        HttpContext http,
        PlaceOrderService orders,
        OrderDetailMapper details,
        IOptions<CheckoutOptions> checkout,
        IOptions<CatalogOptions> catalog,
        CancellationToken cancellationToken)
    {
        if (http.User.CustomerId() is not { } customerId)
        {
            return TypedResults.Unauthorized();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.CheckoutKey is null || request.CheckoutKey == Guid.Empty)
        {
            errors["checkoutKey"] = ["Send the checkout key the page created for this checkout."];
        }

        if (request.ExpectedTotal < 0)
        {
            errors["expectedTotal"] = ["The reviewed total cannot be negative."];
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            errors["lines"] = ["Send the lines you reviewed."];
        }
        else
        {
            for (var i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];
                if (line?.ProductId is null)
                {
                    errors[$"lines[{i}].productId"] = ["Each line needs its product id."];
                }
                else if (line.Quantity is < 1 or > CartLine.MaxQuantity)
                {
                    errors[$"lines[{i}].quantity"] = [$"Quantity must be 1 to {CartLine.MaxQuantity}."];
                }
                else if (line.UnitPrice < 0)
                {
                    errors[$"lines[{i}].unitPrice"] = ["The reviewed unit price cannot be negative."];
                }
            }

            if (request.Lines.Where(l => l?.ProductId is not null).GroupBy(l => l.ProductId).Any(g => g.Count() > 1))
            {
                errors["lines"] = ["Each product appears once."];
            }
        }

        if (!Enum.IsDefined(request.ShippingMethod))
        {
            errors["shippingMethod"] = ["shippingMethod must be standard or express."];
        }

        if (request.ShippingAddress is null)
        {
            errors["shippingAddress"] = ["Enter the shipping address."];
        }
        else
        {
            foreach (var (key, value) in ShippingAddressRules.Validate(request.ShippingAddress, checkout.Value.Country))
            {
                errors[key] = value;
            }
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors, title: "The order is not valid");
        }

        var outcome = await orders.PlaceAsync(customerId, request, cancellationToken);
        var mediaBase = http.Request.PublicBaseUrl(catalog.Value);
        switch (outcome)
        {
            case PlaceOrderOutcome.Created created:
                var order = (await details.LoadAsync(created.OrderId, mediaBase, cancellationToken))!;
                return TypedResults.Created($"/api/v1/orders/{order.Number}", order);
            case PlaceOrderOutcome.AlreadyPlaced placed:
                return TypedResults.Ok(await details.LoadAsync(placed.OrderId, mediaBase, cancellationToken));
            case PlaceOrderOutcome.Changed changed:
                return TypedResults.Json(changed.Problem, statusCode: StatusCodes.Status409Conflict, contentType: "application/problem+json");
            case PlaceOrderOutcome.EmptyCart:
                return TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, type: CheckoutProblemTypes.EmptyCart,
                    title: "Your cart is empty", detail: "Add something to your cart before placing an order.");
            case PlaceOrderOutcome.ShippingMethodNotAvailable notAvailable:
                return ShippingNotAvailable(notAvailable.Message);
            case PlaceOrderOutcome.CheckoutKeyTaken:
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, type: CheckoutProblemTypes.CheckoutKeyTaken,
                    title: "This checkout was already used", detail: "Reload the checkout page and place the order again.");
            default:
                throw new InvalidOperationException($"Unhandled Place order outcome {outcome.GetType().Name}.");
        }
    }

    private static ProblemHttpResult ShippingNotAvailable(string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, type: CheckoutProblemTypes.ShippingMethodNotAvailable,
            title: "This shipping method is not available", detail: detail);

    private static bool TryParseShippingMethod(string? value, out ShippingMethod method)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "standard":
                method = ShippingMethod.Standard;
                return true;
            case "express":
                method = ShippingMethod.Express;
                return true;
            default:
                method = default;
                return false;
        }
    }
}
