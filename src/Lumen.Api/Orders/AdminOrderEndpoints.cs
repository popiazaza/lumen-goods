using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Lumen.Api.Catalog;
using Lumen.Api.Identity;
using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Payments;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Orders;

/// <summary>Problem types the admin order endpoints return, so the UI can tell its 409s apart.</summary>
public static class OrderProblemTypes
{
    /// <summary>409: the line is not waiting for an admin decision (BR-ORD-04): it was decided meanwhile, or never needed one.</summary>
    public const string LineNotAwaitingDecision = "urn:lumen:problem:line-not-awaiting-decision";

    /// <summary>
    /// 409: a send to (or poll of) the partner for this line is on its way; deciding now could cross its answer
    /// (BR-ORD-04). Try again in a minute.
    /// </summary>
    public const string PartnerCallInProgress = "urn:lumen:problem:partner-call-in-progress";
}

/// <summary>
/// The admin orders pages (spec page 10): every order with filters for status and date, the detail with payment events
/// and each partner order's status, and resolving a line that needs attention (BR-ORD-04). Admin role only (BR-ADM-01).
/// </summary>
internal static class AdminOrderEndpoints
{
    /// <summary>The extra <c>status</c> filter value for orders with a line waiting for an admin decision.</summary>
    public const string NeedsAttentionFilter = "needs_attention";

    private static readonly Dictionary<string, OrderStatus> StatusNames = Enum.GetValues<OrderStatus>()
        .ToDictionary(s => JsonNamingPolicy.SnakeCaseLower.ConvertName(s.ToString()), StringComparer.OrdinalIgnoreCase);

    public static RouteGroupBuilder MapAdminOrders(this RouteGroupBuilder api)
    {
        var orders = api.MapGroup("/admin/orders").WithTags("Admin: orders").RequireAuthorization(Policies.Admin);
        orders.MapGet("", ListOrders)
            .WithName("AdminListOrders")
            .WithSummary("All orders, newest first, filtered by status and the date they were placed")
            .WithDescription(
                "status (repeatable, or comma-separated; an order matching any of them is listed): an order status (awaiting_payment, "
                + "payment_processing, paid, completed, partially_refunded, refunded, expired) or needs_attention (a line waits for an "
                + "admin decision). from/to: dates (yyyy-MM-dd, UTC, both inclusive) or date-times with an offset (from inclusive, "
                + "to exclusive), so a page in another time zone can send its own midnight.");
        orders.MapGet("/{number}", GetOrder)
            .WithName("AdminGetOrder")
            .WithSummary("One order with its payment events, partner orders and refunds");
        orders.MapPost("/{number}/lines/{lineId:guid}/resolve", ResolveLine)
            .WithName("AdminResolveOrderLine")
            .WithSummary("Confirm or reject (and refund) a line that needs attention; its partner order is closed");
        return api;
    }

    private static async Task<Results<Ok<PagedResult<AdminOrderSummaryDto>>, ValidationProblem>> ListOrders(
        LumenDbContext db, SellerDirectory sellers, TimeProvider clock, CancellationToken cancellationToken,
        string[]? status = null, string? from = null, string? to = null, string? page = null, string? pageSize = null)
    {
        var errors = new Dictionary<string, string[]>();
        var paging = PageRequest.Parse(page, pageSize, errors);

        var statuses = new List<OrderStatus>();
        var needsAttention = false;
        foreach (var name in (status ?? []).SelectMany(s => (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (string.Equals(name, NeedsAttentionFilter, StringComparison.OrdinalIgnoreCase))
            {
                needsAttention = true;
            }
            else if (StatusNames.TryGetValue(name, out var parsed))
            {
                statuses.Add(parsed);
            }
            else
            {
                errors["status"] = [$"Unknown status '{name}'. Use one of: {string.Join(", ", StatusNames.Keys.Append(NeedsAttentionFilter))}."];
            }
        }

        var fromUtc = ParseBound(from, endOfRange: false, "from", errors);
        var toUtc = ParseBound(to, endOfRange: true, "to", errors);
        if (fromUtc is { } start && toUtc is { } end && start >= end)
        {
            errors["to"] = ["to must be after from."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var query = db.Orders.AsNoTracking();
        if (statuses.Count > 0 || needsAttention)
        {
            query = query.Where(o => statuses.Contains(o.Status)
                                     || (needsAttention && o.Lines.Any(l => l.Status == OrderLineStatus.NeedsAttention)));
        }

        if (fromUtc is { } lower)
        {
            query = query.Where(o => o.CreatedUtc >= lower);
        }

        if (toUtc is { } upper)
        {
            query = query.Where(o => o.CreatedUtc < upper);
        }

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
                Email = db.Users.Where(u => u.Id == o.CustomerId).Select(u => u.Email).FirstOrDefault(),
                o.Total,
                o.RefundedTotal,
                Sources = o.Shipments.Select(s => s.Source).ToList(),
                PaymentMethod = o.Payments.OrderByDescending(p => p.CreatedUtc).Select(p => (PaymentMethod?)p.Method).FirstOrDefault(),
                NeedsAttention = o.Lines.Any(l => l.Status == OrderLineStatus.NeedsAttention),
                RefundPending = o.Refunds.Any(r => r.Status != RefundStatus.Succeeded),
                AwaitingCode = o.Payments.Any(p => p.Status == PaymentStatus.RequiresAction && p.OneTimeCodeExpiresUtc > now),
                // Failed is not final, and at most one payment per order is not, so a Failed one is the current payment.
                PaymentUnsettled = o.Payments.Any(p => p.Status == PaymentStatus.Failed),
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(r => new AdminOrderSummaryDto(
                r.Number, r.Status, OrderStatusText.ForOrder(r.Status, r.RefundPending, r.AwaitingCode, r.PaymentUnsettled), r.CreatedUtc.UtcDateTime, r.Email ?? "", r.Total, r.RefundedTotal,
                sellers.Names(r.Sources), r.PaymentMethod, r.NeedsAttention))
            .ToList();
        return TypedResults.Ok(PagedResult<AdminOrderSummaryDto>.Create(items, paging, total));
    }

    private static async Task<Results<Ok<AdminOrderDto>, NotFound<ProblemDetails>>> GetOrder(
        string number, HttpRequest request, OrderDetailReader reader, IOptions<CatalogOptions> catalog, CancellationToken cancellationToken)
    {
        var order = OrderEndpoints.NormaliseNumber(number) is { } normalised
            ? await reader.ForAdminAsync(normalised, request.PublicBaseUrl(catalog.Value), cancellationToken)
            : null;
        return order is null ? OrderEndpoints.NotFound() : TypedResults.Ok(order);
    }

    /// <summary>
    /// BR-ORD-04: confirm (the goods are coming) or reject (refund it, BR-ORD-03) a line that needs attention, and close
    /// its partner order so the job stops sending or polling it. An accepted partner order closes through
    /// <see cref="PartnerOrderUpdates.ResolveByAdmin"/>; one never answered (sent, outcome unknown) is confirmed (the
    /// admin checked the partner has it) or given up. Runs in one short transaction that locks the order, the partner
    /// order and the line before reading them, so a job run claiming or answering the same partner order meanwhile is
    /// waited for, never overwritten; a conflict that still slips through is a 409, never a 500.
    /// </summary>
    private static async Task<Results<Ok<AdminOrderDto>, NotFound<ProblemDetails>, ValidationProblem, ProblemHttpResult>> ResolveLine(
        string number, Guid lineId, ResolveOrderLineRequest body, ClaimsPrincipal user, LumenDbContext db, OrderDetailReader reader,
        SellerDirectory sellers, TimeProvider clock, HttpRequest request, IOptions<CatalogOptions> catalog, CancellationToken cancellationToken)
    {
        if (body.Decision is not { } resolution || !Enum.IsDefined(resolution))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["decision"] = ["decision must be confirm or reject."] });
        }

        if (OrderEndpoints.NormaliseNumber(number) is not { } normalised
            || await db.Orders.AsNoTracking().Where(o => o.Number == normalised).Select(o => (Guid?)o.Id).SingleOrDefaultAsync(cancellationToken) is not { } orderId)
        {
            return OrderEndpoints.NotFound();
        }

        var note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
        var because = note is null ? "" : $": {note}";

        // Which admin decided is part of every reason it writes, so the order's history says who to ask. The customer
        // never sees it: a refund's customer-facing reason stops before the bracket (OrderDetailMapping.CustomerReason),
        // and line and partner order reasons are admin-only.
        var admin = $"admin {user.SignedInAs()}";

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.LockOrderAsync(orderId, cancellationToken);
            await db.LockLineAndPartnerOrderAsync(orderId, lineId, cancellationToken);
            var now = clock.GetUtcNow();
            var order = await db.Orders
                .Include(o => o.Lines).ThenInclude(l => l.PartnerOrder)
                .Include(o => o.Shipments)
                .AsSplitQuery()
                .SingleAsync(o => o.Id == orderId, cancellationToken);

            if (order.Lines.SingleOrDefault(l => l.Id == lineId) is not { } line)
            {
                return TypedResults.NotFound(new ProblemDetails
                {
                    Title = "Order line not found",
                    Status = StatusCodes.Status404NotFound,
                    Detail = "This order has no such line.",
                });
            }

            if (line.Status != OrderLineStatus.NeedsAttention || line.PartnerOrder is not { } partnerOrder
                || partnerOrder.Status is not (PartnerOrderStatus.Accepted or PartnerOrderStatus.Submitted))
            {
                return NotAwaitingDecision(line.LineNumber);
            }

            var seller = sellers.Name(line.Source);
            if (partnerOrder.ClaimedUntilUtc is { } claimedUntil && claimedUntil > now)
            {
                return TypedResults.Problem(
                    type: OrderProblemTypes.PartnerCallInProgress,
                    title: "A call to the partner is in progress",
                    detail: $"A send to {seller} for this line is in progress; try again in a minute.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var decision = resolution == LineResolution.Confirm ? OrderLineStatus.Confirmed : OrderLineStatus.Rejected;

            // Accepted: the domain's admin resolution closes it. Submitted (sent, never answered): the admin vouches the
            // partner has it (Confirmed) or gives it up; both are final, so the job stops sending it.
            var partnerOrderAfter = partnerOrder.Status == PartnerOrderStatus.Accepted
                ? PartnerOrderUpdates.ResolveByAdmin(partnerOrder.Status, line.Status, decision).PartnerOrder
                : OrderTransitions.PartnerOrder.Move(partnerOrder.Status,
                    decision == OrderLineStatus.Confirmed ? PartnerOrderStatus.Confirmed : PartnerOrderStatus.GivenUp);

            if (decision == OrderLineStatus.Rejected)
            {
                var paymentId = await LineRejection.SucceededPaymentIdAsync(db, order.Id, cancellationToken);
                if (LineRejection.Reject(order, line, paymentId, $"Not supplied by {seller} (rejected by {admin}{because})", now) is { } refund)
                {
                    db.Refunds.Add(refund);
                }

                line.StatusReason = LineRejection.Truncate($"Rejected by {admin}{because}");
            }
            else
            {
                line.Status = OrderTransitions.Line.Move(line.Status, OrderLineStatus.Confirmed);
                line.StatusReason = LineRejection.Truncate($"Confirmed by {admin}{because}");
            }

            partnerOrder.Status = partnerOrderAfter;
            partnerOrder.DecidedUtc = now;
            partnerOrder.NextAttemptUtc = null;
            partnerOrder.Reason = LineRejection.Truncate($"Closed by {admin}, line {(decision == OrderLineStatus.Confirmed ? "confirmed" : "rejected")}{because}");
            LineRejection.DeriveOrderStatus(order);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return NotAwaitingDecision(null);
        }

        db.ChangeTracker.Clear();
        var updated = await reader.ForAdminAsync(normalised, request.PublicBaseUrl(catalog.Value), cancellationToken);
        return TypedResults.Ok(updated!);
    }

    private static ProblemHttpResult NotAwaitingDecision(int? lineNumber) => TypedResults.Problem(
        type: OrderProblemTypes.LineNotAwaitingDecision,
        title: "This line does not need a decision",
        detail: lineNumber is { } n
            ? $"Line {n.ToString(CultureInfo.InvariantCulture)} is not delayed (any more); only a delayed line can be resolved. Reload the order."
            : "The line changed while it was being resolved. Reload the order.",
        statusCode: StatusCodes.Status409Conflict);

    /// <summary>
    /// A date (yyyy-MM-dd, a UTC day; as the end of a range it includes that whole day) or a date-time with an offset,
    /// converted to UTC. Null when empty; a problem is added for anything else.
    /// </summary>
    internal static DateTimeOffset? ParseBound(string? value, bool endOfRange, string name, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return endOfRange ? start.AddDays(1) : start;
        }

        if (text.Contains('T', StringComparison.Ordinal)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var moment))
        {
            return moment.ToUniversalTime();
        }

        errors[name] = [$"{name} must be a date (yyyy-MM-dd) or an ISO 8601 date-time."];
        return null;
    }
}
