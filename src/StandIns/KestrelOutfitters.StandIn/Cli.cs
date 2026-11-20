using System.Globalization;
using KestrelOutfitters.StandIn.Catalogue;
using KestrelOutfitters.StandIn.Data;

namespace KestrelOutfitters.StandIn;

/// <summary>
/// Subcommands of the same binary that change Kestrel's data on the same database, for demos
/// ("prices and stock can change at any time") without adding endpoints to a fixed contract.
/// </summary>
internal static class Cli
{
    private const string Usage = """
        Usage: dotnet KestrelOutfitters.StandIn.dll <command>

          list                                       Show every product with price, active flag and stock
          set-stock  <ref> <units> [<yyyy-MM-dd>]    Set units on hand and the optional restock date
          set-price  <ref> "<EUR, e.g. 1.249,00>"    Set the price (Kestrel format); updates Last-Modified
          set-active <ref> <Y|N>                     Activate or deactivate; updates Last-Modified

        Example: docker compose exec kestrel dotnet KestrelOutfitters.StandIn.dll set-stock K-7731 0
        """;

    private static readonly string[] Commands = ["list", "set-stock", "set-price", "set-active", "help"];

    public static bool IsCommand(string[] args) => args.Length > 0 && Commands.Contains(args[0], StringComparer.Ordinal);

    public static async Task<int> RunAsync(
        string[] args,
        string databasePath,
        TimeProvider time,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        if (args is ["help"])
        {
            await stdout.WriteLineAsync(Usage);
            return 0;
        }

        await using var db = KestrelDb.Open(databasePath);
        await Database.PrepareAsync(db, databasePath, time.GetUtcNow(), cancellationToken);
        var store = new CatalogueStore(db, time);

        var (exitCode, message) = args switch
        {
            ["list"] => (0, FormatList(await store.ListAsync(cancellationToken))),
            ["set-stock", var productRef, var units] => await SetStockAsync(store, productRef, units, null, cancellationToken),
            ["set-stock", var productRef, var units, var restock] => await SetStockAsync(store, productRef, units, restock, cancellationToken),
            ["set-price", var productRef, var price] => await SetPriceAsync(store, productRef, price, cancellationToken),
            ["set-active", var productRef, var flag] => await SetActiveAsync(store, productRef, flag, cancellationToken),
            _ => (1, Usage),
        };

        await (exitCode == 0 ? stdout : stderr).WriteLineAsync(message);
        return exitCode;
    }

    private static async Task<(int, string)> SetStockAsync(CatalogueStore store, string productRef, string units, string? restock, CancellationToken cancellationToken)
    {
        if (!int.TryParse(units, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
        {
            return (1, $"units must be a whole number of 0 or more, not '{units}'.");
        }

        DateOnly? restockDate = null;
        if (restock is not null)
        {
            if (!DateOnly.TryParseExact(restock, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return (1, $"restock date must be yyyy-MM-dd, not '{restock}'.");
            }

            restockDate = date;
        }

        return await store.SetStockAsync(productRef, count, restockDate, cancellationToken)
            ? (0, $"{productRef}: {count} units, restock date {restockDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "(none)"}. "
                + "The feed's Last-Modified is unchanged: stock is not part of the feed.")
            : NotFound(productRef);
    }

    private static async Task<(int, string)> SetPriceAsync(CatalogueStore store, string productRef, string price, CancellationToken cancellationToken)
    {
        if (!EuroPrice.TryParse(price, out var amount))
        {
            return (1, $"price must use Kestrel's format, for example 14,95 or 1.249,00, not '{price}'.");
        }

        return await store.SetPriceAsync(productRef, amount, cancellationToken)
            ? (0, $"{productRef}: price {EuroPrice.Format(amount)} EUR. Last-Modified is now {await LastModifiedAsync(store, cancellationToken)}.")
            : NotFound(productRef);
    }

    private static async Task<(int, string)> SetActiveAsync(CatalogueStore store, string productRef, string flag, CancellationToken cancellationToken)
    {
        if (flag is not ("Y" or "N"))
        {
            return (1, $"active must be Y or N, not '{flag}'.");
        }

        return await store.SetActiveAsync(productRef, flag == "Y", cancellationToken)
            ? (0, $"{productRef}: active {flag}. Last-Modified is now {await LastModifiedAsync(store, cancellationToken)}.")
            : NotFound(productRef);
    }

    private static (int, string) NotFound(string productRef) => (1, $"No product with ref '{productRef}'.");

    private static async Task<string> LastModifiedAsync(CatalogueStore store, CancellationToken cancellationToken) =>
        (await store.GetLastModifiedAsync(cancellationToken)).ToString("R", CultureInfo.InvariantCulture);

    private static string FormatList(List<Product> products)
    {
        var lines = products.Select(p => string.Create(CultureInfo.InvariantCulture,
            $"{p.Ref,-8} {p.Group,-8} {(p.Active ? "Y" : "N"),-6} {EuroPrice.Format(p.PriceEur),10} {p.Units,5}  {p.RestockDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",-10}  {p.NameEn ?? p.NameDe}"));
        return string.Join(Environment.NewLine, lines.Prepend($"{"REF",-8} {"GROUP",-8} {"ACTIVE",-6} {"PRICE EUR",10} {"UNITS",5}  {"RESTOCK",-10}  NAME"));
    }
}
