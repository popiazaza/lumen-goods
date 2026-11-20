using System.Globalization;
using Lumen.Api.Catalog;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Lumen.Api.Carts;

/// <summary>A cart change the rules refused: an RFC 9457 problem with a stable <see cref="Type"/> the UI can switch on.</summary>
public sealed record CartProblem(int Status, string Type, string Title, string Detail, IReadOnlyDictionary<string, object?> Extensions)
{
    public ProblemHttpResult ToResult() =>
        TypedResults.Problem(detail: Detail, statusCode: Status, title: Title, type: Type, extensions: Extensions.ToDictionary());
}

/// <summary>
/// The cart's error answers. 422 for a change the rules refuse (BR-CART-03), 404 for a line that is not in the
/// caller's cart (including a line in someone else's cart: it is never reachable, and the answer does not say it exists).
/// </summary>
/// <example>
/// <code>
/// { "type": "urn:lumen:problem:cart-quantity-limit", "title": "Only 2 available", "status": 422,
///   "detail": "Only 2 of this item are available, and your cart already has 1.", "maxQuantity": 2, "inCart": 1 }
/// </code>
/// </example>
public static class CartProblems
{
    /// <summary>The product is unknown, no longer sold, or out of stock. Extension <c>reason</c>: not_found | no_longer_available | out_of_stock.</summary>
    public const string ProductUnavailableType = "urn:lumen:problem:cart-product-unavailable";

    /// <summary>
    /// The line would exceed 10 or the known available quantity. Extensions: <c>maxQuantity</c> (most units the line may
    /// hold in total) and <c>inCart</c> (units it holds now).
    /// </summary>
    public const string QuantityLimitType = "urn:lumen:problem:cart-quantity-limit";

    public const string LineNotFoundType = "urn:lumen:problem:cart-line-not-found";

    /// <summary>The 400 validation message for a quantity outside 1 to 10 (BR-CART-03).</summary>
    public const string QuantityRangeMessage = "Choose a quantity from 1 to 10 (maximum 10 per item).";

    public static CartProblem ProductNotFound(Guid productId) => new(
        StatusCodes.Status422UnprocessableEntity, ProductUnavailableType, "Product not found",
        $"There is no product with id '{productId}'.",
        new Dictionary<string, object?> { ["reason"] = "not_found" });

    public static CartProblem NotSellable(Product product) => new(
        StatusCodes.Status422UnprocessableEntity, ProductUnavailableType, "No longer available",
        $"{product.Name} can no longer be bought.",
        new Dictionary<string, object?> { ["reason"] = "no_longer_available" });

    public static CartProblem OutOfStock(Product product) => new(
        StatusCodes.Status422UnprocessableEntity, ProductUnavailableType, "Out of stock",
        $"{product.Name} is out of stock.",
        new Dictionary<string, object?> { ["reason"] = "out_of_stock" });

    public static CartProblem MaximumPerItem(int inCart) => new(
        StatusCodes.Status422UnprocessableEntity, QuantityLimitType, $"Maximum {CartLine.MaxQuantity} per item",
        inCart > 0
            ? $"You can have up to {CartLine.MaxQuantity} of an item, and your cart already has {inCart}."
            : $"You can have up to {CartLine.MaxQuantity} of an item.",
        new Dictionary<string, object?> { ["maxQuantity"] = CartLine.MaxQuantity, ["inCart"] = inCart });

    public static CartProblem OnlyAvailable(int available, int inCart) => new(
        StatusCodes.Status422UnprocessableEntity, QuantityLimitType,
        string.Create(CultureInfo.InvariantCulture, $"Only {available} available"),
        inCart > 0
            ? string.Create(CultureInfo.InvariantCulture, $"Only {available} of this item are available, and your cart already has {inCart}.")
            : string.Create(CultureInfo.InvariantCulture, $"Only {available} of this item are available."),
        new Dictionary<string, object?> { ["maxQuantity"] = Math.Min(available, CartLine.MaxQuantity), ["inCart"] = inCart });

    public static CartProblem LineNotFound(Guid lineId) => new(
        StatusCodes.Status404NotFound, LineNotFoundType, "Cart line not found",
        $"Your cart has no line with id '{lineId}'.",
        new Dictionary<string, object?>());
}
