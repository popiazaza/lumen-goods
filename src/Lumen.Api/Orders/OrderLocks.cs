using Lumen.Api.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Lumen.Api.Orders;

/// <summary>
/// Row locks for changes to a paid order. Everything that changes a paid order's lines, refunds or partner orders (the
/// partner order job, an admin resolving a line) locks the order row first, then the partner order, inside one short
/// transaction with no partner or payment I/O. So two rejections of the same seller's lines cannot both miss that the
/// seller's shipment is now fully rejected, and an admin decision and a late partner answer cannot both win.
/// </summary>
internal static class OrderLocks
{
    /// <summary>Locks the order row until the transaction ends, waiting for a holder to finish. False when there is no such order.</summary>
    public static async Task<bool> LockOrderAsync(this LumenDbContext db, Guid orderId, CancellationToken cancellationToken) =>
        (await db.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "Orders" WHERE "Id" = {orderId} FOR UPDATE""")
            .ToListAsync(cancellationToken)).Count == 1;

    /// <summary>Locks the order row if nobody holds it; false when it is locked (try again on the next run) or missing.</summary>
    public static async Task<bool> TryLockOrderAsync(this LumenDbContext db, Guid orderId, CancellationToken cancellationToken) =>
        (await db.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "Orders" WHERE "Id" = {orderId} FOR UPDATE SKIP LOCKED""")
            .ToListAsync(cancellationToken)).Count == 1;

    /// <summary>
    /// Locks an order line and its partner order (after the order), waiting for a job run that holds them, so what is
    /// read afterwards is what will be written over.
    /// </summary>
    public static async Task LockLineAndPartnerOrderAsync(this LumenDbContext db, Guid orderId, Guid lineId, CancellationToken cancellationToken)
    {
        await db.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "OrderLines" WHERE "Id" = {lineId} AND "OrderId" = {orderId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
        await db.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "PartnerOrders" WHERE "OrderLineId" = {lineId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
    }

    /// <summary>Locks those of these partner order rows nobody holds; returns the ids it locked.</summary>
    public static async Task<List<Guid>> TryLockPartnerOrdersAsync(this LumenDbContext db, Guid[] partnerOrderIds, CancellationToken cancellationToken) =>
        await db.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "PartnerOrders" WHERE "Id" = ANY({partnerOrderIds}) FOR UPDATE SKIP LOCKED""")
            .ToListAsync(cancellationToken);

    /// <summary>Locks a partner order row (after its order), so the job's claim step skips it meanwhile.</summary>
    public static async Task LockPartnerOrderAsync(this LumenDbContext db, Guid partnerOrderId, CancellationToken cancellationToken) =>
        await db.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "PartnerOrders" WHERE "Id" = {partnerOrderId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
}
