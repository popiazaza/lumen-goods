using Lumen.Api.Catalog;
using Lumen.Api.Identity;
using Lumen.Api.Infrastructure.RateLimiting;
using Lumen.Api.Orders;
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

    /// <summary>
    /// 409: the checkout key is already used by another customer's order. Resending the same key always gives this
    /// again, so the client must start the review again with a new key.
    /// </summary>
    public const string CheckoutKeyTaken = "urn:lumen:problem:checkout-key-taken";

    /// <summary>
    /// 409 (BR-CHK-03): the customer already has the maximum number of unpaid orders. Paying one, or letting one expire,
    /// makes room; the detail says so.
    /// </summary>
    public const string TooManyUnpaidOrders = "urn:lumen:problem:too-many-unpaid-orders";
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
            .RequireRateLimiting(RateLimitPolicies.PlaceOrder)
            .Produces<PriceChangedProblem>(StatusCodes.Status409Conflict, "application/problem+json")
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity).ProducesProblem(StatusCodes.Status429TooManyRequests);
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

    /// <summary>
    /// The built-in validation (AddValidation) has already refused a missing checkout key, address or lines, an empty
    /// line list, a line without a product id and a quantity outside 1 to 10. What is left here is what attributes cannot
    /// say: values that need the whole request (too many lines, duplicates, the address rules for the launch country) and
    /// the total, which is keyed by its JSON name.
    /// </summary>
    private static async Task<Results<Created<OrderDetailDto>, Ok<OrderDetailDto>, JsonHttpResult<PriceChangedProblem>, ValidationProblem, ProblemHttpResult, UnauthorizedHttpResult>> PlaceOrder(
        PlaceOrderRequest request,
        HttpContext http,
        PlaceOrderService orders,
        OrderDetailReader details,
        IOptions<CheckoutOptions> checkout,
        IOptions<CatalogOptions> catalog,
        CancellationToken cancellationToken)
    {
        if (http.User.CustomerId() is not { } customerId)
        {
            return TypedResults.Unauthorized();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.CheckoutKey == Guid.Empty)
        {
            errors["checkoutKey"] = ["Send the checkout key the page created for this checkout."];
        }

        if (request.ExpectedTotal is null)
        {
            errors["expectedTotal"] = ["Send the total you reviewed."];
        }
        else if (request.ExpectedTotal < 0)
        {
            errors["expectedTotal"] = ["The reviewed total cannot be negative."];
        }

        var lines = request.Lines!;
        for (var i = 0; i < lines.Count; i++)
        {
            // Validation skips a null element of the list (JSON "lines": [null]).
            if (lines[i] is not { } line)
            {
                errors[$"lines[{i}]"] = ["Each line needs its product id and quantity."];
            }
            else if (line.UnitPrice < 0)
            {
                errors[$"lines[{i}].unitPrice"] = ["The reviewed unit price cannot be negative."];
            }
        }

        if (lines.Count > PlaceOrderRequest.MaxLines)
        {
            errors["lines"] = [$"An order can have at most {PlaceOrderRequest.MaxLines} lines. Remove some from your cart and review it again."];
        }
        else if (lines.Where(l => l is not null).GroupBy(l => l.ProductId).Any(g => g.Count() > 1))
        {
            errors["lines"] = ["Each product appears once."];
        }

        if (!Enum.IsDefined(request.ShippingMethod))
        {
            errors["shippingMethod"] = ["shippingMethod must be standard or express."];
        }

        foreach (var (key, value) in ShippingAddressRules.Validate(request.ShippingAddress!, checkout.Value.Country))
        {
            errors[key] = value;
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
                // The same OrderDetailDto as GET /orders/{number}, from the same reader.
                var order = (await details.ByIdAsync(created.OrderId, mediaBase, cancellationToken))!;
                return TypedResults.Created($"/api/v1/orders/{order.Number}", order);
            case PlaceOrderOutcome.AlreadyPlaced placed:
                return TypedResults.Ok((await details.ByIdAsync(placed.OrderId, mediaBase, cancellationToken))!);
            case PlaceOrderOutcome.Changed changed:
                return TypedResults.Json(changed.Problem, statusCode: StatusCodes.Status409Conflict, contentType: "application/problem+json");
            case PlaceOrderOutcome.EmptyCart:
                return TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, type: CheckoutProblemTypes.EmptyCart,
                    title: "Your cart is empty", detail: "Add something to your cart before placing an order.");
            case PlaceOrderOutcome.ShippingMethodNotAvailable notAvailable:
                return ShippingNotAvailable(notAvailable.Message);
            case PlaceOrderOutcome.CheckoutKeyTaken:
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, type: CheckoutProblemTypes.CheckoutKeyTaken,
                    title: "This checkout was already used",
                    detail: "This checkout key was already used for a different order. Start the review again with a new checkout key.");
            case PlaceOrderOutcome.TooManyUnpaidOrders tooMany:
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, type: CheckoutProblemTypes.TooManyUnpaidOrders,
                    title: "Too many unpaid orders",
                    detail: $"You already have {tooMany.Max} unpaid orders. Pay for one of them in Your orders, or wait until one "
                        + "expires, then place this order again. Nothing was created.");
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
