using System.Globalization;
using HarborSupply.StandIn.Catalogue;
using HarborSupply.StandIn.Http;
using HarborSupply.StandIn.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HarborSupply.StandIn.Cli;

/// <summary>
/// Demo-data commands run by the same binary against the same database, so partner data can change
/// "at any time" during a demo without adding a single HTTP endpoint to the contract.
/// <code>dotnet HarborSupply.StandIn.dll set-stock HSC-10021 0</code>
/// Settings use the --Section:Key=value form, for example --Harbor:DatabasePath=/tmp/harbor.db.
/// </summary>
internal static class AdminCli
{
    private const string Usage = """
        Usage: dotnet HarborSupply.StandIn.dll <command>
          list                                       print every item
          set-stock <item_code> <on_hand>            whole number, 0 or more
          set-price <item_code> <cents|null>         whole cents, or null for "price on request"
          set-discontinued <item_code> <true|false>
        Every change bumps the item's updated_at, so Lumen picks it up through updated_since.
        """;

    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal) { "list", "set-stock", "set-price", "set-discontinued" };

    public static bool IsCommand(string[] args) => args.Length > 0 && Commands.Contains(args[0]);

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var settings = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var command = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

        var builder = Host.CreateApplicationBuilder(settings);
        builder.Logging.ClearProviders();
        builder.Services.AddHarborCore();
        using var host = builder.Build();

        await DatabaseInitializer.InitializeAsync(host.Services);
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<HarborDbContext>>().CreateDbContextAsync();
        var now = host.Services.GetRequiredService<TimeProvider>().GetUtcNow();

        switch (command)
        {
            case ["list"]:
                await ListAsync(db, output);
                return 0;
            case ["set-stock", var code, var value] when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var onHand):
                return await UpdateAsync(db, code, item => item.SetOnHand(onHand, now), output, error);
            case ["set-price", var code, "null"]:
                return await UpdateAsync(db, code, item => item.SetPrice(null, now), output, error);
            case ["set-price", var code, var value] when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var cents):
                return await UpdateAsync(db, code, item => item.SetPrice(cents, now), output, error);
            case ["set-discontinued", var code, var value] when bool.TryParse(value, out var discontinued):
                return await UpdateAsync(db, code, item => item.SetDiscontinued(discontinued, now), output, error);
            default:
                await error.WriteLineAsync(Usage);
                return 2;
        }
    }

    private static async Task<int> UpdateAsync(HarborDbContext db, string itemCode, Action<CatalogItem> change, TextWriter output, TextWriter error)
    {
        var item = await db.Items.SingleOrDefaultAsync(i => i.ItemCode == itemCode);
        if (item is null)
        {
            await error.WriteLineAsync($"No item with item_code {itemCode}.");
            return 1;
        }

        var before = item.UpdatedAt;
        change(item);
        await db.SaveChangesAsync();

        await output.WriteLineAsync(item.UpdatedAt == before ? "No change." : "Updated:");
        await output.WriteLineAsync(Describe(item));
        return 0;
    }

    private static async Task ListAsync(HarborDbContext db, TextWriter output)
    {
        var items = await db.Items.AsNoTracking().OrderBy(i => i.ItemCode).ToListAsync();
        await output.WriteLineAsync($"{"ITEM_CODE",-10} {"DEPT",-10} {"PRICE",8} {"ON_HAND",7} {"STATUS",-9} {"DISC",-5} {"UPDATED_AT",-20}  TITLE");
        foreach (var item in items)
        {
            await output.WriteLineAsync(Describe(item));
        }
    }

    private static string Describe(CatalogItem item) =>
        $"{item.ItemCode,-10} {item.Dept,-10} {item.UnitPriceCents?.ToString(CultureInfo.InvariantCulture) ?? "null",8} {item.OnHand,7} " +
        $"{InventoryResponse.For(item.OnHand).Status,-9} {(item.Discontinued ? "yes" : "no"),-5} {HarborJson.FormatTimestamp(item.UpdatedAt),-20}  {item.Title}";
}
