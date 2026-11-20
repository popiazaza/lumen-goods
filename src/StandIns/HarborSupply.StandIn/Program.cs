using HarborSupply.StandIn;
using HarborSupply.StandIn.Catalogue;
using HarborSupply.StandIn.Cli;
using HarborSupply.StandIn.Http;
using HarborSupply.StandIn.Media;
using HarborSupply.StandIn.Orders;
using HarborSupply.StandIn.Persistence;

// Stand-in for Harbor Supply Co.'s published JSON REST API (the partner's sandbox).
// "dotnet HarborSupply.StandIn.dll set-stock HSC-10021 0" and friends run the demo-data CLI instead of the server.
if (AdminCli.IsCommand(args))
{
    return await AdminCli.RunAsync(args, Console.Out, Console.Error);
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHarborCore();
builder.Services.AddSingleton<FixedWindowRateLimiter>();
builder.Services.AddSingleton<CatalogueStore>();
builder.Services.AddSingleton<OrderService>();
builder.Services.AddSingleton<ProductImageRenderer>();

var app = builder.Build();
await DatabaseInitializer.InitializeAsync(app.Services);

// Order matters: error bodies wrap everything; the key is checked before the rate limit so 401s are not counted.
app.UseHarborErrorBodies();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseMiddleware<RateLimitMiddleware>();

app.MapCatalogue();
app.MapOrders();
app.MapMedia();

await app.RunAsync();
return 0;
