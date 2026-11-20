using Lumen.Api.Catalog;
using Lumen.Api.Identity;
using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Partners;
using Lumen.Api.Partners.Harbor;
using Lumen.Api.Partners.Kestrel;
using Lumen.Api.Partners.Sync;
using Lumen.Domain.Catalog;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Lumen.IntegrationTests.Partners;

/// <summary>
/// Partner tests delete and rewrite every Harbor and Kestrel product, so they get a PostgreSQL of their own
/// (<see cref="PartnerDatabase"/>) instead of the assembly-wide one: other features' carts and orders reference partner
/// products there, and a reset would collide with them. Within this collection the tests run one at a time, because
/// they share this database; the collection itself runs alongside the rest of the suite.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PartnerSyncCollection : ICollectionFixture<PartnerDatabase>
{
    public const string Name = "Partner sync (own database)";
}

/// <summary>A PostgreSQL 18 container used only by the partner sync tests, started once for their collection.</summary>
public sealed class PartnerDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18.7-alpine3.24").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

/// <summary>
/// The real API on the partner tests' own database with the partners' HTTP replaced by fakes (via the named clients'
/// primary handler, so credentials, rate limiting and resilience still run). Migrations and seeding run as in docker
/// compose. Jobs are off; tests call RunOnceAsync.
/// </summary>
internal sealed class PartnerApiFactory(PartnerDatabase database, FakeHarbor harbor, FakeKestrel kestrel, Action<IWebHostBuilder>? configure)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Lumen", database.ConnectionString);
        builder.UseSetting("Auth:CookieSecurePolicy", "SameAsRequest");
        builder.UseSetting("Seed:Enabled", "true");
        PartnerTestHost.ConfigurePartners(builder, harbor, kestrel);
        configure?.Invoke(builder);
    }
}

internal static class PartnerTestHost
{
    public static WebApplicationFactory<Program> Create(PartnerDatabase database, FakeHarbor harbor, FakeKestrel kestrel, Action<IWebHostBuilder>? configure = null) =>
        new PartnerApiFactory(database, harbor, kestrel, configure);

    public static void ConfigurePartners(IWebHostBuilder builder, FakeHarbor harbor, FakeKestrel kestrel)
    {
        builder.UseSetting("Jobs:Enabled", "false");
        builder.UseSetting("Partners:Harbor:BaseUrl", "http://harbor.test");
        builder.UseSetting("Partners:Harbor:ApiKey", FakeHarbor.Key);
        builder.UseSetting("Partners:Harbor:Resilience:MaxRetries", "1");
        builder.UseSetting("Partners:Harbor:Resilience:RetryBaseDelay", "00:00:00.010");
        builder.UseSetting("Partners:Kestrel:BaseUrl", "http://kestrel.test");
        builder.UseSetting("Partners:Kestrel:Username", FakeKestrel.User);
        builder.UseSetting("Partners:Kestrel:Password", FakeKestrel.Password);
        builder.UseSetting("Partners:Kestrel:Resilience:MaxRetries", "1");
        builder.UseSetting("Partners:Kestrel:Resilience:RetryBaseDelay", "00:00:00.010");
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(HarborCatalogSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => harbor);
            services.AddHttpClient(HarborCatalogSource.ImageHttpClientName).ConfigurePrimaryHttpMessageHandler(() => harbor);
            services.AddHttpClient(KestrelCatalogSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => kestrel);
        });
    }

    public static PartnerSyncJob Job(this WebApplicationFactory<Program> app, string partner) =>
        app.Services.GetRequiredKeyedService<PartnerSyncJob>(partner);

    public static async Task<T> WithDbAsync<T>(this WebApplicationFactory<Program> app, Func<LumenDbContext, Task<T>> work)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<LumenDbContext>());
    }

    public static async Task<T> WithSyncAsync<T>(this WebApplicationFactory<Program> app, Func<PartnerSyncService, PartnerRegistry, Task<T>> work)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<PartnerSyncService>(), scope.ServiceProvider.GetRequiredService<PartnerRegistry>());
    }

    public static Task<Dictionary<string, Product>> ProductsAsync(this WebApplicationFactory<Program> app, string source) =>
        app.WithDbAsync(db => db.Products.AsNoTracking().Where(p => p.Source == source).ToDictionaryAsync(p => p.SourceProductId));

    public static Task<PartnerSyncState> StateAsync(this WebApplicationFactory<Program> app, string partner) =>
        app.WithDbAsync(db => db.PartnerSyncStates.AsNoTracking().SingleAsync(s => s.Partner == partner));

    public static Task<List<PartnerSyncError>> ErrorsAsync(this WebApplicationFactory<Program> app, string partner) =>
        app.WithDbAsync(db => db.PartnerSyncErrors.AsNoTracking().Where(e => e.Partner == partner).OrderByDescending(e => e.OccurredUtc).ToListAsync());

    public static Task RequestRefreshAsync(this WebApplicationFactory<Program> app, string partner, bool confirmRemovals = false) =>
        app.WithSyncAsync((sync, _) => sync.RequestRefreshAsync(partner, confirmRemovals, CancellationToken.None));

    /// <summary>Row versions (xmin) and change times of every partner product: equal before and after means nothing was written.</summary>
    public static Task<Dictionary<Guid, (uint Version, DateTimeOffset LastUpdatedUtc)>> RowVersionsAsync(this WebApplicationFactory<Program> app) =>
        app.WithDbAsync(db => db.Products.AsNoTracking()
            .Where(p => p.Source == ProductSources.Harbor || p.Source == ProductSources.Kestrel)
            .ToDictionaryAsync(p => p.Id, p => (p.Version, p.LastUpdatedUtc)));

    public static Task<int> ResetPartnerDataAsync(this WebApplicationFactory<Program> app) =>
        app.WithDbAsync(async db =>
        {
            var removed = await db.Products.Where(p => p.Source == ProductSources.Harbor || p.Source == ProductSources.Kestrel).ExecuteDeleteAsync();
            await db.PartnerSyncErrors.ExecuteDeleteAsync();
            await db.PartnerSyncStates.ExecuteDeleteAsync();
            return removed;
        });

    public static async Task<ApiSession> SignInAsync(this WebApplicationFactory<Program> app, string email, string password)
    {
        var session = new ApiSession(app.CreateClient());
        await session.StartAsync();
        (await session.PostAsJsonAsync("/api/v1/auth/sign-in", new SignInRequest(email, password))).EnsureSuccessStatusCode();

        // Antiforgery tokens are bound to the user: fetch one for the signed-in session.
        await session.StartAsync();
        return session;
    }

    /// <summary>The default partner catalogues used by most tests.</summary>
    public static void SeedDefaults(FakeHarbor harbor, FakeKestrel kestrel)
    {
        harbor.Set(
            FakeHarbor.Item("HSC-1", "BAGS", 4999, "LIMITED", onHand: 3, colour: "Olive"),
            FakeHarbor.Item("HSC-2", "GARDEN", 1500, "AVAILABLE"),
            FakeHarbor.Item("HSC-3", "TEES", null, "AVAILABLE"),
            FakeHarbor.Item("HSC-4", "MISC", 2500, "LIMITED", onHand: 8),
            FakeHarbor.Item("HSC-5", "CAMP", 9900, "SOLD_OUT", discontinued: true));

        kestrel.SetFeed(
            FakeKestrel.Product("K-1", "JACKETS", "1.249,00", nameDe: "Expeditionsparka", nameEn: "Expedition Parka",
                descEn: "<p>Warm <b>down</b>.</p><script>alert(1)</script>"),
            FakeKestrel.Product("K-2", "SOCKS", "14,95", nameDe: "Wandersocken", large: false),
            FakeKestrel.Product("K-3", "GLOVES", "39,00", nameEn: "Gloves"),
            FakeKestrel.Product("K-4", "LAMPS", "12.5", active: "N", nameEn: "Lamp"),
            FakeKestrel.Product("K-5", "TENTS", "299,00", nameEn: "Tent"),
            FakeKestrel.Product("K-6", "PACKS", "89,00", nameEn: "Pack"));
        kestrel.Stock.Clear();
        kestrel.Stock["K-1"] = 2;
        kestrel.Stock["K-5"] = 12;
        kestrel.Stock["K-6"] = 0;
    }
}
