using System.Text;
using System.Xml.Linq;
using KestrelOutfitters.StandIn.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KestrelOutfitters.StandIn.Orders;

/// <summary>Contract: POST /orders (asynchronous, 202/409/400) and GET /orders/{ticket} (polling only).</summary>
internal static class OrderEndpoints
{
    // Tickets count up from the number in the contract's example.
    private const int FirstTicketNumber = 5521;

    public static void MapOrders(this IEndpointRouteBuilder app)
    {
        app.MapPost("/orders", PlaceOrderAsync);
        app.MapGet("/orders/{ticket}", GetOrderStatusAsync);
    }

    private static async Task<IResult> PlaceOrderAsync(
        HttpContext context,
        IDbContextFactory<KestrelDb> dbFactory,
        ContractRandom random,
        TimeProvider time,
        IOptions<KestrelOutfittersOptions> options)
    {
        var cancellationToken = context.RequestAborted;
        var (request, error) = await PurchaseOrderReader.ReadAsync(context.Request, cancellationToken);
        if (request is null)
        {
            return InvalidRequest(error!);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var refs = request.Lines.Select(l => l.Ref).ToList();
        var known = await db.Products.Where(p => refs.Contains(p.Ref)).Select(p => p.Ref).ToListAsync(cancellationToken);
        var unknown = refs.Except(known, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            // Inactive products are known refs: they are accepted here and rejected as out of stock later.
            return InvalidRequest($"Unknown ref: {string.Join(", ", unknown)}");
        }

        // BEGIN IMMEDIATE (Microsoft.Data.Sqlite's default) serialises the reference check and the ticket number.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var existingTicket = await db.Orders
            .Where(o => o.Reference == request.Reference)
            .Select(o => o.Ticket)
            .FirstOrDefaultAsync(cancellationToken);
        if (existingTicket is not null)
        {
            return Xml(StatusCodes.Status409Conflict,
                new XElement("Error", new XAttribute("code", "DUPLICATE_REFERENCE"), new XAttribute("ticket", existingTicket)));
        }

        var settings = options.Value;
        var now = time.GetUtcNow();
        var order = new PurchaseOrder
        {
            Ticket = $"KT-{FirstTicketNumber + await db.Orders.CountAsync(cancellationToken)}",
            Reference = request.Reference,
            Lines = request.Lines,
            Delivery = request.Delivery,
            ReceivedAt = now,
            DecideAt = now + random.Between(settings.OrderDecisionDelayMin, settings.OrderDecisionDelayMax),
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // 202 only means "received"; fulfilment is decided later and only visible by polling.
        return Xml(StatusCodes.Status202Accepted,
            new XElement("OrderAck", new XAttribute("status", "RECEIVED"), new XAttribute("ticket", order.Ticket)));
    }

    private static async Task<IResult> GetOrderStatusAsync(
        string ticket,
        IDbContextFactory<KestrelDb> dbFactory,
        OrderDecider decider,
        CancellationToken cancellationToken)
    {
        await decider.DecideDueOrdersAsync(cancellationToken);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Ticket == ticket, cancellationToken);
        if (order is null)
        {
            return Results.StatusCode(StatusCodes.Status404NotFound);
        }

        return Xml(StatusCodes.Status200OK, new XElement("OrderStatus",
            new XAttribute("ticket", order.Ticket),
            new XAttribute("state", order.StateName),
            order.Reason is null ? null : new XAttribute("reason", order.Reason)));
    }

    private static IResult InvalidRequest(string message) =>
        Xml(StatusCodes.Status400BadRequest,
            new XElement("Error", new XAttribute("code", "INVALID_REQUEST"), new XAttribute("message", message)));

    // XElement does the escaping, so partner-supplied text (references, refs) cannot break the document.
    private static IResult Xml(int statusCode, XElement body) =>
        Results.Text(body.ToString(SaveOptions.DisableFormatting), "application/xml; charset=utf-8", Encoding.UTF8, statusCode);
}
