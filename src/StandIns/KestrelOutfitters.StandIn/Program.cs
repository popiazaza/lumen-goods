using KestrelOutfitters.StandIn;
using KestrelOutfitters.StandIn.Catalogue;
using KestrelOutfitters.StandIn.Data;
using KestrelOutfitters.StandIn.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// One binary, two modes. CLI subcommands change Kestrel's data on the same database for demos,
// because the contract is fixed and must not grow extra HTTP endpoints.
if (Cli.IsCommand(args))
{
    var settings = KestrelOutfittersOptions.From(WebApplication.CreateBuilder().Configuration);
    return await Cli.RunAsync(args, settings.DatabasePath, TimeProvider.System, Console.Out, Console.Error);
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<KestrelOutfittersOptions>()
    .BindConfiguration(KestrelOutfittersOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<KestrelOutfittersOptions>, KestrelOutfittersOptionsValidator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ContractRandom>();
builder.Services.AddDbContextFactory<KestrelDb>((services, db) =>
    KestrelDb.Configure(db, services.GetRequiredService<IOptions<KestrelOutfittersOptions>>().Value.DatabasePath));
builder.Services.AddSingleton<OrderDecider>();
builder.Services.AddHostedService<OrderDecisionService>();
// Malformed input must get the partner's error formats, never a framework-generated 400.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = false);

var app = builder.Build();

await using (var db = await app.Services.GetRequiredService<IDbContextFactory<KestrelDb>>().CreateDbContextAsync())
{
    var databasePath = app.Services.GetRequiredService<IOptions<KestrelOutfittersOptions>>().Value.DatabasePath;
    await Database.PrepareAsync(db, databasePath, app.Services.GetRequiredService<TimeProvider>().GetUtcNow(), CancellationToken.None);
}

// The contract defines its own (often empty) error bodies: no developer exception page, no ProblemDetails.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    ExceptionHandler = context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return Task.CompletedTask;
    },
});
app.UseMiddleware<BasicAuthenticationMiddleware>();

app.MapFeed();
app.MapImages();
app.MapStock();
app.MapOrders();

await app.RunAsync();
return 0;

/// <summary>Entry point; public so the conformance tests can host the stand-in in memory.</summary>
public partial class Program;
