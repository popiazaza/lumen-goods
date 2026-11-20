using KestrelOutfitters.StandIn.Catalogue;
using KestrelOutfitters.StandIn.Data;
using Microsoft.EntityFrameworkCore;

namespace KestrelOutfitters.StandIn.Orders;

/// <summary>
/// Contract: orders stay RECEIVED for 10 to 30 seconds, then become CONFIRMED or REJECTED. An order is rejected
/// when any line exceeds stock at that moment; stock is reduced only on confirmation.
/// Runs from <see cref="OrderDecisionService"/> on a timer and before every read that shows order state or stock,
/// so what Lumen sees never lags behind the clock.
/// </summary>
internal sealed class OrderDecider(IDbContextFactory<KestrelDb> dbFactory, TimeProvider time, ILogger<OrderDecider> logger)
{
    public async Task DecideDueOrdersAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = time.GetUtcNow();
        if (!await Due(db, now).AnyAsync(cancellationToken))
        {
            return;
        }

        // Microsoft.Data.Sqlite begins with BEGIN IMMEDIATE: the database write lock serialises this with order
        // intake, a concurrent decider run and the CLI process, so stock is never handed out twice.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var orders = await Due(db, now).OrderBy(o => o.DecideAt).ThenBy(o => o.Id).ToListAsync(cancellationToken);
        var refs = orders.SelectMany(o => o.Lines).Select(l => l.Ref).Distinct().ToList();
        var products = await db.Products.Where(p => refs.Contains(p.Ref)).ToDictionaryAsync(p => p.Ref, cancellationToken);

        foreach (var order in orders)
        {
            Decide(order, products, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private void Decide(PurchaseOrder order, Dictionary<string, Product> products, DateTimeOffset now)
    {
        // The whole order is rejected; the reason names the first short line, as in the contract's example.
        var shortLine = order.Lines.FirstOrDefault(line => line.Quantity > AvailableUnits(products[line.Ref]));
        if (shortLine is not null)
        {
            order.Reject($"OUT_OF_STOCK:{shortLine.Ref}", now);
        }
        else
        {
            foreach (var line in order.Lines)
            {
                products[line.Ref].Units -= line.Quantity;
            }

            order.Confirm(now);
        }

        logger.LogInformation("Order {Ticket} ({Reference}) decided: {State} {Reason}", order.Ticket, order.Reference, order.StateName, order.Reason ?? "");
    }

    // An inactive product cannot be supplied. It counts as zero stock, so it is rejected with the contract's
    // own OUT_OF_STOCK reason rather than a reason code the contract does not define.
    private static int AvailableUnits(Product product) => product.Active ? product.Units : 0;

    private static IQueryable<PurchaseOrder> Due(KestrelDb db, DateTimeOffset now) =>
        db.Orders.Where(o => o.State == OrderState.Received && o.DecideAt <= now);
}
