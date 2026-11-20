using System.Text.Json;
using HarborSupply.StandIn.Http;
using HarborSupply.StandIn.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HarborSupply.StandIn.Orders;

/// <summary>What happened to an order request; the endpoint turns each case into its status code.</summary>
internal abstract record OrderOutcome
{
    public sealed record Accepted(string OrderId, string Status) : OrderOutcome;

    public sealed record Replayed(string OrderId, string Status) : OrderOutcome;

    public sealed record Invalid(string Message) : OrderOutcome;

    public sealed record InvalidItem(string ItemCode, string Reason) : OrderOutcome;

    public sealed record InsufficientStock(string ItemCode, int Available) : OrderOutcome;
}

internal sealed class OrderService(IDbContextFactory<HarborDbContext> dbFactory, TimeProvider clock)
{
    private const int FirstOrderNumber = 10001;
    private const string AcceptedStatus = "ACCEPTED";

    /// <summary>
    /// Order of checks: a reused external_ref answers with the original order before anything else is looked at
    /// (the contract: "returns 200 with the original order, and does not order again"); then 400, 422, 409.
    /// </summary>
    public async Task<OrderOutcome> PlaceAsync(OrderRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ExternalRef))
        {
            return new OrderOutcome.Invalid("external_ref is required.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // SQLite starts this as BEGIN IMMEDIATE, taking the write lock up front. The ref check, stock check and
        // decrement therefore run as one unit against concurrent orders and against the CLI in another process.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var existing = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.ExternalRef == request.ExternalRef, cancellationToken);
        if (existing is not null)
        {
            return new OrderOutcome.Replayed(existing.OrderId, existing.Status);
        }

        if (request.Validate() is { } problem)
        {
            return new OrderOutcome.Invalid(problem);
        }

        var lines = request.Lines!.Select(l => (ItemCode: l!.ItemCode!, Qty: l.Qty!.Value)).ToList();
        var codes = lines.Select(l => l.ItemCode).Distinct().ToList();
        var items = await db.Items.Where(i => codes.Contains(i.ItemCode)).ToDictionaryAsync(i => i.ItemCode, cancellationToken);

        foreach (var (itemCode, _) in lines)
        {
            if (Unorderable(items.GetValueOrDefault(itemCode)) is { } reason)
            {
                return new OrderOutcome.InvalidItem(itemCode, reason);
            }
        }

        // Lines for the same item are checked together; long, so huge quantities cannot overflow past the check.
        var wanted = lines.GroupBy(l => l.ItemCode).Select(g => (ItemCode: g.Key, Qty: g.Sum(l => (long)l.Qty))).ToList();
        foreach (var (itemCode, qty) in wanted)
        {
            if (qty > items[itemCode].OnHand)
            {
                return new OrderOutcome.InsufficientStock(itemCode, items[itemCode].OnHand);
            }
        }

        var now = clock.GetUtcNow();
        foreach (var (itemCode, qty) in wanted)
        {
            items[itemCode].SetOnHand(items[itemCode].OnHand - (int)qty, now);
        }

        var lastNumber = await db.Orders.MaxAsync(o => (int?)o.Number, cancellationToken) ?? FirstOrderNumber - 1;
        var order = new HarborOrder
        {
            Number = lastNumber + 1,
            ExternalRef = request.ExternalRef,
            Status = AcceptedStatus,
            CreatedAt = now.UtcDateTime,
            RequestJson = JsonSerializer.Serialize(request, HarborJson.Options),
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new OrderOutcome.Accepted(order.OrderId, order.Status);
    }

    private static string? Unorderable(CatalogItem? item) => item switch
    {
        null => "does not exist",
        { Discontinued: true } => "is discontinued",
        { UnitPriceCents: null } => "has no price and cannot be sold online",
        _ => null,
    };
}
