using System.Security.Claims;
using Lumen.Api.Catalog;
using Lumen.Api.Identity;
using Lumen.Api.Infrastructure.Data;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Orders;

/// <summary>
/// The customer's order history and order page (spec page 8; BR-ORD-02). Signed-in customers only, and only their own
/// orders: another customer's order number answers 404, exactly like a number that does not exist, so order numbers
/// reveal nothing (BR-ADM-01).
/// </summary>
internal static class OrderEndpoints
{
    public static RouteGroupBuilder MapCustomerOrders(this RouteGroupBuilder api)
    {
        var orders = api.MapGroup("/orders").WithTags("Orders").RequireAuthorization();
        orders.MapGet("", ListOrders)
            .WithName("ListMyOrders")
            .WithSummary("The signed-in customer's orders, newest first, with the shops their items came from");
        orders.MapGet("/{number}", GetOrder)
            .WithName("GetMyOrder")
            .WithSummary("One of the signed-in customer's orders: lines by seller with their status, shipments, totals, payment and refunds");
        return api;
    }

    private static async Task<Results<Ok<PagedResult<OrderSummaryDto>>, ValidationProblem, UnauthorizedHttpResult>> ListOrders(
        ClaimsPrincipal user, LumenDbContext db, SellerDirectory sellers, PaymentWindowPolicy paymentWindow, TimeProvider clock,
        CancellationToken cancellationToken, string? page = null, string? pageSize = null)
    {
        if (user.CustomerId() is not { } customerId)
        {
            return TypedResults.Unauthorized();
        }

        var errors = new Dictionary<string, string[]>();
        var paging = PageRequest.Parse(page, pageSize, errors);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var query = db.Orders.AsNoTracking().Where(o => o.CustomerId == customerId);
        var now = clock.GetUtcNow();
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(o => o.CreatedUtc).ThenByDescending(o => o.Id)
            .Skip(paging.Skip).Take(paging.PageSize)
            .Select(o => new
            {
                o.Number,
                o.Status,
                o.CreatedUtc,
                o.Total,
                o.RefundedTotal,
                o.FailedPaymentAttempts,
                ItemCount = o.Lines.Sum(l => l.Quantity),
                Sources = o.Shipments.Select(s => s.Source).ToList(),
                RefundPending = o.Refunds.Any(r => r.Status != RefundStatus.Succeeded),
                // At most one payment of an order is not final, so a RequiresAction one is the current payment.
                AwaitingCode = o.Payments.Any(p => p.Status == PaymentStatus.RequiresAction && p.OneTimeCodeExpiresUtc > now),
                // Failed is not final, and at most one payment per order is not, so a Failed one is the current payment.
                PaymentUnsettled = o.Payments.Any(p => p.Status == PaymentStatus.Failed),
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(r => new OrderSummaryDto(
                r.Number, r.Status, OrderStatusText.ForOrder(r.Status, r.RefundPending, r.AwaitingCode, r.PaymentUnsettled), r.CreatedUtc.UtcDateTime, r.Total, r.RefundedTotal, r.ItemCount,
                sellers.Names(r.Sources),
                // The same test as the order page's CanPay (OrderDetailReader), so the list and the page agree.
                CanPay: r.Status == OrderStatus.AwaitingPayment && paymentWindow.CanStartAttempt(r.FailedPaymentAttempts, r.CreatedUtc, now),
                AwaitingOneTimeCode: r.Status == OrderStatus.PaymentProcessing && r.AwaitingCode))
            .ToList();
        return TypedResults.Ok(PagedResult<OrderSummaryDto>.Create(items, paging, total));
    }

    private static async Task<Results<Ok<OrderDetailDto>, NotFound<ProblemDetails>, UnauthorizedHttpResult>> GetOrder(
        string number, ClaimsPrincipal user, HttpRequest request, OrderDetailReader reader, IOptions<CatalogOptions> catalog,
        CancellationToken cancellationToken)
    {
        if (user.CustomerId() is not { } customerId)
        {
            return TypedResults.Unauthorized();
        }

        var normalised = NormaliseNumber(number);
        var order = normalised is null
            ? null
            : await reader.ForCustomerAsync(normalised, customerId, request.PublicBaseUrl(catalog.Value), cancellationToken);
        return order is null ? NotFound() : TypedResults.Ok(order);
    }

    /// <summary>Upper-cased and checked (<see cref="OrderNumberGenerator.IsWellFormed"/>); null for anything that cannot be an order number.</summary>
    public static string? NormaliseNumber(string? number)
    {
        var candidate = number?.Trim().ToUpperInvariant();
        return OrderNumberGenerator.IsWellFormed(candidate) ? candidate : null;
    }

    public static NotFound<ProblemDetails> NotFound() => TypedResults.NotFound(new ProblemDetails
    {
        Title = "Order not found",
        Status = StatusCodes.Status404NotFound,
        Detail = "There is no order with this number.",
    });
}
