using System.Globalization;
using System.Text;
using KestrelOutfitters.StandIn.Data;
using KestrelOutfitters.StandIn.Orders;
using Microsoft.EntityFrameworkCore;

namespace KestrelOutfitters.StandIn.Catalogue;

/// <summary>Contract: GET /stock?refs=K-7731,K-7732. Semicolon CSV, header row first, at most 20 refs.</summary>
internal static class StockEndpoint
{
    private const int MaxRefs = 20;
    private const string Header = "ref;units;restock_date";

    // RFC 4180 (the text/csv registration) ends records with CRLF.
    private const string LineEnd = "\r\n";

    public static void MapStock(this IEndpointRouteBuilder app) => app.MapGet("/stock", GetStockAsync);

    private static async Task<IResult> GetStockAsync(
        HttpContext context,
        IDbContextFactory<KestrelDb> dbFactory,
        OrderDecider decider,
        CancellationToken cancellationToken)
    {
        // Read by hand: framework model binding would answer bad input with its own 400 body.
        var refs = context.Request.Query["refs"]
            .SelectMany(value => (value ?? "").Split(','))
            .Select(r => r.Trim())
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (refs.Count == 0)
        {
            return PlainText400("The refs query parameter is required, for example /stock?refs=K-7731,K-7732.");
        }

        if (refs.Count > MaxRefs)
        {
            return PlainText400($"At most {MaxRefs} refs per request; this request has {refs.Count}.");
        }

        // Orders whose decision time has passed are settled first, so stock never lags behind a decision.
        await decider.DecideDueOrdersAsync(cancellationToken);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var stock = await db.Products.AsNoTracking()
            .Where(p => refs.Contains(p.Ref))
            .Select(p => new { p.Ref, p.Units, p.RestockDate })
            .ToDictionaryAsync(p => p.Ref, cancellationToken);

        var csv = new StringBuilder(Header).Append(LineEnd);
        foreach (var productRef in refs)
        {
            // Unknown refs are left out, with no error. Inactive products are still listed.
            if (stock.TryGetValue(productRef, out var row))
            {
                csv.Append(row.Ref).Append(';')
                    .Append(row.Units.ToString(CultureInfo.InvariantCulture)).Append(';')
                    .Append(row.RestockDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    .Append(LineEnd);
            }
        }

        return Results.Text(csv.ToString(), "text/csv");
    }

    private static IResult PlainText400(string message) =>
        Results.Text(message, "text/plain; charset=utf-8", Encoding.UTF8, StatusCodes.Status400BadRequest);
}
