using System.Net.Http.Json;
using System.Text.Json;
using Lumen.Api.Partners.Sync;
using Lumen.Domain.Catalog;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Lumen.IntegrationTests.Partners;

/// <summary>
/// BR-SYNC-01 over simulated time: the sync jobs run as their hosted loop would (a wake-up every 5 s, each deciding
/// from the shipped intervals whether anything is due) on a fake clock, and the storefront API is asked after each
/// wake-up whether a partner's change shows yet.
/// </summary>
[Collection(PartnerSyncCollection.Name)]
public sealed class PartnerSyncScheduleTests(PartnerDatabase database) : IAsyncLifetime
{
    private static readonly TimeSpan WakeUp = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(15);

    private readonly FakeHarbor _harbor = new();
    private readonly FakeKestrel _kestrel = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero));
    private WebApplicationFactory<Program> _app = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _app = PartnerTestHost.Create(database, _harbor, _kestrel,
            builder => builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(_clock)));
        await _app.ResetPartnerDataAsync();
        PartnerTestHost.SeedDefaults(_harbor, _kestrel);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.ResetPartnerDataAsync();
        await _app.DisposeAsync();
    }

    private async Task<JsonElement> ProductAsync(Guid id)
    {
        using var client = _app.CreateClient();
        return await client.GetFromJsonAsync<JsonElement>($"/api/v1/products/{id}", Ct);
    }

    [Fact]
    [Trait("BR", "BR-SYNC-01")]
    public async Task BR_SYNC_01_a_harbor_price_and_a_kestrel_stock_change_show_in_the_shop_within_15_minutes()
    {
        (await _app.Job(ProductSources.Harbor).RunOnceAsync(Ct)).Catalogue!.Result.Should().Be(CatalogueRunResult.Applied);
        (await _app.Job(ProductSources.Kestrel).RunOnceAsync(Ct)).Catalogue!.Result.Should().Be(CatalogueRunResult.Applied);
        var tote = (await _app.ProductsAsync(ProductSources.Harbor))["HSC-1"].Id;
        var parka = (await _app.ProductsAsync(ProductSources.Kestrel))["K-1"].Id;
        (await ProductAsync(tote)).GetProperty("price").GetProperty("amount").GetDecimal().Should().Be(49.99m);
        (await ProductAsync(parka)).GetProperty("availability").GetProperty("status").GetString().Should().Be("low_stock");

        // t0, part-way through every interval: Harbor reprices the tote (a new updated_at), Kestrel sells out the parka
        // (stock only: its feed does not change).
        _clock.Advance(TimeSpan.FromSeconds(42));
        var t0 = _clock.GetUtcNow();
        _harbor.Set(
            FakeHarbor.Item("HSC-1", "BAGS", 5499, "LIMITED", onHand: 3, colour: "Olive", updatedAt: "2026-11-20T10:00:40Z"),
            FakeHarbor.Item("HSC-2", "GARDEN", 1500, "AVAILABLE"),
            FakeHarbor.Item("HSC-3", "TEES", null, "AVAILABLE"),
            FakeHarbor.Item("HSC-4", "MISC", 2500, "LIMITED", onHand: 8),
            FakeHarbor.Item("HSC-5", "CAMP", 9900, "SOLD_OUT", discontinued: true));
        _kestrel.Stock["K-1"] = 0;

        TimeSpan? priceShown = null;
        TimeSpan? stockShown = null;
        while (_clock.GetUtcNow() - t0 < Budget && (priceShown is null || stockShown is null))
        {
            _clock.Advance(WakeUp);
            await _app.Job(ProductSources.Harbor).RunOnceAsync(Ct);
            await _app.Job(ProductSources.Kestrel).RunOnceAsync(Ct);

            if (priceShown is null && (await ProductAsync(tote)).GetProperty("price").GetProperty("amount").GetDecimal() == 54.99m)
            {
                priceShown = _clock.GetUtcNow() - t0;
            }

            if (stockShown is null && (await ProductAsync(parka)).GetProperty("availability") is var availability
                && availability.GetProperty("status").GetString() == "out_of_stock")
            {
                availability.GetProperty("quantity").GetInt32().Should().Be(0);
                stockShown = _clock.GetUtcNow() - t0;
            }
        }

        // With the shipped intervals: the next Harbor incremental pass (60 s) and the next Kestrel stock read (5 min).
        priceShown.Should().NotBeNull().And.BeLessThan(Budget).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(60) + WakeUp);
        stockShown.Should().NotBeNull().And.BeLessThan(Budget).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(5) + WakeUp);
    }
}
