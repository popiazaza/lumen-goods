using Lumen.Api.Catalog;
using Lumen.Api.Partners.Sync;
using Lumen.Domain.Catalog;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Lumen.IntegrationTests.Partners;

/// <summary>The sync jobs end to end: fake partners over HTTP, the real engine, the real products table.</summary>
[Collection(PartnerSyncCollection.Name)]
public sealed class PartnerSyncTests(PartnerDatabase database) : IAsyncLifetime
{
    private const string Harbor = ProductSources.Harbor;
    private const string Kestrel = ProductSources.Kestrel;

    /// <summary>The fake feed's generated="2026-11-18T10:00:00+01:00".</summary>
    private static readonly DateTimeOffset Generated = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeHarbor _harbor = new();
    private readonly FakeKestrel _kestrel = new();
    private WebApplicationFactory<Program> _app = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _app = PartnerTestHost.Create(database, _harbor, _kestrel);
        await _app.ResetPartnerDataAsync();
        PartnerTestHost.SeedDefaults(_harbor, _kestrel);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.ResetPartnerDataAsync();
        await _app.DisposeAsync();
    }

    private async Task SyncBothAsync()
    {
        (await _app.Job(Harbor).RunOnceAsync(Ct)).Catalogue!.Result.Should().Be(CatalogueRunResult.Applied);
        (await _app.Job(Kestrel).RunOnceAsync(Ct)).Catalogue!.Result.Should().Be(CatalogueRunResult.Applied);
    }

    [Fact]
    [Trait("BR", "BR-SYNC-01")]
    public async Task BR_SYNC_01_a_harbor_and_kestrel_sync_fills_the_catalogue_with_storefront_data()
    {
        var harborRun = await _app.Job(Harbor).RunOnceAsync(Ct);
        var kestrelRun = await _app.Job(Kestrel).RunOnceAsync(Ct);

        harborRun.Catalogue.Should().BeEquivalentTo(new { Result = CatalogueRunResult.Applied, Inserted = 4 });
        kestrelRun.Catalogue.Should().BeEquivalentTo(new { Result = CatalogueRunResult.Applied, Inserted = 5 });
        kestrelRun.Stock.Should().BeEquivalentTo(new { Succeeded = true });

        var policy = new AvailabilityPolicy();
        var harbor = await _app.ProductsAsync(Harbor);
        harbor.Keys.Should().BeEquivalentTo(new[] { "HSC-1", "HSC-3", "HSC-4", "HSC-5" }, "HSC-2 is in an unmapped department");

        var tote = harbor["HSC-1"].ToDto(policy, "http://shop.test");
        tote.Should().BeEquivalentTo(new
        {
            Source = "harbor",
            Name = "Harbor HSC-1",
            Description = "Summary of HSC-1.",
            Category = "bags",
            Price = new PriceDto(49.99m, "USD"),
            Attributes = new Dictionary<string, string> { ["color"] = "Olive" },
            Availability = new AvailabilityDto("low_stock", 3),
            Sellable = true,
            LastUpdatedUtc = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc),
        });
        tote.Images.Should().Equal(
            new ImageDto(ProductMapping.ImageUrl("http://shop.test", harbor["HSC-1"].Id, 0, "http://harbor.test/media/HSC-1/front.png"), true),
            new ImageDto(ProductMapping.ImageUrl("http://shop.test", harbor["HSC-1"].Id, 1, "http://harbor.test/media/HSC-1/side.png"), false));
        harbor["HSC-1"].Images[0].SourceUrl.Should().Be("http://harbor.test/media/HSC-1/front.png");

        harbor["HSC-3"].ToDto(policy, "").Should().BeEquivalentTo(new { Price = (PriceDto?)null, Sellable = false, Availability = new AvailabilityDto("in_stock", null) });
        harbor["HSC-3"].IsListed.Should().BeTrue("price on request is still listed");
        harbor["HSC-4"].ToDto(policy, "").Availability.Should().Be(new AvailabilityDto("in_stock", 8), "LIMITED with 8 on hand is in stock");
        harbor["HSC-5"].Should().BeEquivalentTo(new { IsActive = false, IsListed = false, Quantity = 0 });

        var kestrel = await _app.ProductsAsync(Kestrel);
        kestrel.Keys.Should().BeEquivalentTo(new[] { "K-1", "K-2", "K-4", "K-5", "K-6" }, "K-3 is in an unmapped group");

        var parka = kestrel["K-1"];
        parka.Should().BeEquivalentTo(new
        {
            Name = "Expedition Parka",
            Category = "apparel",
            PriceUsd = 1355.17m,
            SourcePrice = 1249.00m,
            SourceCurrency = "EUR",
            FxRateToUsd = 1.085m,
            Quantity = 2,
            IsListed = true,
            OnHand = (int?)null,
            Reserved = 0,
        });
        parka.LastUpdatedUtc.Should().BeAfter(Generated, "the first stock read changed it after the feed");
        parka.Description.Should().Be("<p>Warm <b>down</b>.</p>");
        parka.Images.Should().Equal(new ProductImage("http://kestrel.test/img/K-1_l.png", true));
        parka.ToDto(policy, "").Availability.Should().Be(new AvailabilityDto("low_stock", 2));

        kestrel["K-2"].Name.Should().Be("Wandersocken");
        kestrel["K-2"].LastUpdatedUtc.Should().Be(Generated, "a change from the feed is dated by the feed's generated time, in UTC");
        kestrel["K-2"].PriceUsd.Should().Be(16.22m);
        kestrel["K-2"].ToDto(policy, "").Availability.Should().Be(new AvailabilityDto("out_of_stock", null), "missing from the stock CSV means unknown");
        kestrel["K-4"].Should().BeEquivalentTo(new { IsActive = false, IsListed = false, PriceUsd = (decimal?)null });
        kestrel["K-5"].ToDto(policy, "").Availability.Should().Be(new AvailabilityDto("in_stock", 12));
        kestrel["K-6"].ToDto(policy, "").Availability.Should().Be(new AvailabilityDto("out_of_stock", 0));

        var harborState = await _app.StateAsync(Harbor);
        harborState.Should().BeEquivalentTo(new { LiveProductCount = 3, LastError = (string?)null, Watermark = "2026-11-01T08:00:00.0000000Z" });
        harborState.LastSuccessUtc.Should().NotBeNull();
        harborState.LastFullSyncUtc.Should().NotBeNull();

        var kestrelState = await _app.StateAsync(Kestrel);
        kestrelState.Should().BeEquivalentTo(new { LiveProductCount = 4, LastError = (string?)null, Watermark = _kestrel.LastModified });
        kestrelState.StockRefreshedUtc.Should().NotBeNull();
        (await _app.ErrorsAsync(Kestrel)).Should().ContainSingle(e => e.Operation == "data" && e.Message.Contains("K-4"));
    }

    [Fact]
    public async Task A_second_run_with_unchanged_data_writes_nothing()
    {
        await SyncBothAsync();
        var before = await _app.RowVersionsAsync();
        var errorsBefore = (await _app.ErrorsAsync(Kestrel)).Count;

        // "Refresh now" forces the most thorough run: Harbor full pass, Kestrel unconditional feed plus all stock.
        await _app.RequestRefreshAsync(Harbor);
        await _app.RequestRefreshAsync(Kestrel);
        _kestrel.ClearCalls();
        var harborRun = await _app.Job(Harbor).RunOnceAsync(Ct);
        var kestrelRun = await _app.Job(Kestrel).RunOnceAsync(Ct);

        harborRun.Catalogue.Should().BeEquivalentTo(new { Result = CatalogueRunResult.Applied, Written = 0, Unchanged = 4 });
        kestrelRun.Catalogue.Should().BeEquivalentTo(new { Result = CatalogueRunResult.Applied, Written = 0, Unchanged = 5 });
        kestrelRun.Stock.Should().BeEquivalentTo(new { Succeeded = true, Updated = 0 });
        _kestrel.Calls.Should().Contain(c => c.Uri.AbsolutePath == "/feed/products.xml" && c.Header("If-Modified-Since") == null);

        (await _app.RowVersionsAsync()).Should().BeEquivalentTo(before);
        (await _app.ErrorsAsync(Kestrel)).Should().HaveCount(errorsBefore, "a known data problem is not reported again");
        (await _app.StateAsync(Harbor)).RefreshRequestedUtc.Should().BeNull("the run served the request");
        (await _app.StateAsync(Kestrel)).RefreshRequestedUtc.Should().BeNull();
    }

    [Fact]
    public async Task A_kestrel_304_writes_nothing()
    {
        await SyncBothAsync();
        var before = await _app.RowVersionsAsync();
        _kestrel.ClearCalls();

        var outcome = await _app.WithSyncAsync((sync, partners) => sync.SyncCatalogueAsync(partners.Get(Kestrel), full: false, Ct));

        outcome.Result.Should().Be(CatalogueRunResult.NotModified);
        _kestrel.Calls.Should().ContainSingle().Which.Header("If-Modified-Since").Should().Be(_kestrel.LastModified);
        (await _app.RowVersionsAsync()).Should().BeEquivalentTo(before);
    }

    [Fact]
    [Trait("BR", "BR-SYNC-01")]
    public async Task BR_SYNC_01_a_harbor_change_arrives_through_an_incremental_pass()
    {
        await SyncBothAsync();
        _harbor.Set(
            FakeHarbor.Item("HSC-1", "BAGS", 5499, "LIMITED", onHand: 1, colour: "Olive", updatedAt: "2026-11-02T08:00:00Z"),
            FakeHarbor.Item("HSC-3", "TEES", null, "AVAILABLE"),
            FakeHarbor.Item("HSC-4", "MISC", 2500, "LIMITED", onHand: 8),
            FakeHarbor.Item("HSC-5", "CAMP", 9900, "SOLD_OUT", discontinued: true));
        _harbor.ClearCalls();

        var outcome = await _app.WithSyncAsync((sync, partners) => sync.SyncCatalogueAsync(partners.Get(Harbor), full: false, Ct));

        outcome.Should().BeEquivalentTo(new { Result = CatalogueRunResult.Applied, Updated = 1, Unlisted = 0 });
        _harbor.Calls.Should().NotBeEmpty().And.AllSatisfy(c => c.Query("updated_since").Should().Be("2026-11-01T07:55:00Z"));
        var tote = (await _app.ProductsAsync(Harbor))["HSC-1"];
        tote.Should().BeEquivalentTo(new { PriceUsd = 54.99m, Quantity = 1, LastUpdatedUtc = new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero) });
        (await _app.StateAsync(Harbor)).Watermark.Should().Be("2026-11-02T08:00:00.0000000Z");
    }

    [Fact]
    [Trait("BR", "BR-SYNC-02")]
    public async Task BR_SYNC_02_when_partners_are_down_the_last_good_products_stay_and_the_errors_are_recorded()
    {
        await SyncBothAsync();
        var before = await _app.RowVersionsAsync();
        var lastSuccess = (await _app.StateAsync(Kestrel)).LastSuccessUtc;
        _harbor.Down = true;
        _kestrel.Down = true;
        await _app.RequestRefreshAsync(Harbor);
        await _app.RequestRefreshAsync(Kestrel);

        var harborRun = await _app.Job(Harbor).RunOnceAsync(Ct);
        var kestrelRun = await _app.Job(Kestrel).RunOnceAsync(Ct);

        harborRun.Catalogue!.Result.Should().Be(CatalogueRunResult.Failed);
        kestrelRun.Catalogue!.Result.Should().Be(CatalogueRunResult.Failed);
        kestrelRun.Stock!.Succeeded.Should().BeFalse();
        (await _app.RowVersionsAsync()).Should().BeEquivalentTo(before, "the shop keeps the last good data");
        (await _app.ProductsAsync(Kestrel)).Values.Count(p => p.IsListed).Should().Be(4);

        var state = await _app.StateAsync(Kestrel);
        state.LastError.Should().Contain("503");
        state.LastSuccessUtc.Should().Be(lastSuccess);
        state.LiveProductCount.Should().Be(4);
        (await _app.ErrorsAsync(Kestrel)).Should().Contain(e => e.Operation == "catalogue" && e.StatusCode == 503);
        (await _app.ErrorsAsync(Harbor)).Should().Contain(e => e.Operation == "catalogue" && e.StatusCode == 503);
    }

    private static string[] TwelveTents(string price = "299,00") =>
        [.. Enumerable.Range(1, 12).Select(i => FakeKestrel.Product($"T-{i:00}", "TENTS", price, nameEn: $"Tent {i}"))];

    [Fact]
    [Trait("BR", "BR-SYNC-02")]
    public async Task BR_SYNC_02_mass_removals_are_held_back_while_the_rest_of_the_feed_is_applied_until_an_admin_confirms()
    {
        _kestrel.SetFeed(TwelveTents());
        foreach (var i in Enumerable.Range(1, 12))
        {
            _kestrel.Stock[$"T-{i:00}"] = 4;
        }

        await _app.Job(Kestrel).RunOnceAsync(Ct);

        // The partner now lists one tent, at a new price, and a new product: 11 of 12 listed products are missing.
        _kestrel.SetFeed(FakeKestrel.Product("T-01", "TENTS", "349,00", nameEn: "Tent 1"), FakeKestrel.Product("K-NEW", "LAMPS", "19,00", nameEn: "Lamp"));
        _kestrel.LastModified = "Thu, 19 Nov 2026 09:00:00 GMT";
        _kestrel.Stock["T-01"] = 1;
        await _app.RequestRefreshAsync(Kestrel);
        var held = await _app.Job(Kestrel).RunOnceAsync(Ct);

        held.Catalogue.Should().BeEquivalentTo(new { Result = CatalogueRunResult.Applied, Inserted = 1, Updated = 1, Unlisted = 0, HeldRemovals = 11 });
        held.Stock!.Succeeded.Should().BeTrue();
        var products = await _app.ProductsAsync(Kestrel);
        products["T-01"].Should().BeEquivalentTo(new { PriceUsd = 378.67m, Quantity = 1 }, "price and stock changes still arrive");
        products["K-NEW"].IsListed.Should().BeTrue();
        products.Values.Count(p => p.IsListed).Should().Be(13, "the 11 missing tents stay listed until confirmed");
        var state = await _app.StateAsync(Kestrel);
        state.Should().BeEquivalentTo(new { HeldRemovals = 11, Watermark = _kestrel.LastModified, LiveProductCount = 13 });
        state.LastError.Should().Contain("11 removals held back").And.Contain("confirmRemovals=true");
        (await _app.ErrorsAsync(Kestrel)).Should().ContainSingle(e => e.Message.Contains("held back"));

        // Later runs are normal: the feed is not refetched and rejected over and over.
        var next = await _app.WithSyncAsync((sync, partners) => sync.SyncCatalogueAsync(partners.Get(Kestrel), full: false, Ct));
        next.Result.Should().Be(CatalogueRunResult.NotModified);

        await _app.RequestRefreshAsync(Kestrel, confirmRemovals: true);
        var confirmed = await _app.Job(Kestrel).RunOnceAsync(Ct);

        confirmed.Catalogue.Should().BeEquivalentTo(new { Result = CatalogueRunResult.Applied, Unlisted = 11, HeldRemovals = 0 });
        (await _app.ProductsAsync(Kestrel)).Values.Count(p => p.IsListed).Should().Be(2);
        (await _app.StateAsync(Kestrel)).Should().BeEquivalentTo(new
        {
            HeldRemovals = 0, LiveProductCount = 2, LastError = (string?)null, RefreshRequestedUtc = (DateTimeOffset?)null, RemovalsConfirmedUtc = (DateTimeOffset?)null,
        });
    }

    [Fact]
    [Trait("BR", "BR-SYNC-02")]
    public async Task BR_SYNC_02_an_empty_feed_never_empties_the_shop()
    {
        await SyncBothAsync();
        _kestrel.SetFeed();
        _kestrel.LastModified = "Thu, 19 Nov 2026 09:00:00 GMT";

        await _app.RequestRefreshAsync(Kestrel);
        var run = await _app.Job(Kestrel).RunOnceAsync(Ct);

        run.Catalogue.Should().BeEquivalentTo(new { Result = CatalogueRunResult.Applied, Unlisted = 0, HeldRemovals = 4 });
        run.Catalogue!.Error.Should().Contain("empty");
        (await _app.ProductsAsync(Kestrel)).Values.Count(p => p.IsListed).Should().Be(4);
    }

    [Fact]
    [Trait("BR", "BR-SYNC-02")]
    public async Task BR_SYNC_02_a_small_catalogue_may_shrink_by_more_than_half()
    {
        await SyncBothAsync();
        _kestrel.SetFeed(FakeKestrel.Product("K-1", "JACKETS", "1.249,00", nameEn: "Expedition Parka"));
        _kestrel.LastModified = "Thu, 19 Nov 2026 09:00:00 GMT";

        await _app.RequestRefreshAsync(Kestrel);
        var run = await _app.Job(Kestrel).RunOnceAsync(Ct);

        run.Catalogue.Should().BeEquivalentTo(new { Result = CatalogueRunResult.Applied, Unlisted = 3, HeldRemovals = 0 }, "3 removals is below the guard's minimum of 10");
        (await _app.StateAsync(Kestrel)).Should().BeEquivalentTo(new { HeldRemovals = 0, LiveProductCount = 1, LastError = (string?)null });
    }

    [Fact]
    [Trait("BR", "BR-SYNC-01")]
    [Trait("BR", "BR-CAT-02")]
    public async Task BR_SYNC_01_products_missing_from_a_good_full_catalogue_leave_the_shop_and_stop_being_sellable()
    {
        await SyncBothAsync();
        _kestrel.SetFeed(
            FakeKestrel.Product("K-1", "JACKETS", "1.249,00", nameDe: "Expeditionsparka", nameEn: "Expedition Parka", descEn: "<p>Warm <b>down</b>.</p><script>alert(1)</script>"),
            FakeKestrel.Product("K-2", "SOCKS", "14,95", nameDe: "Wandersocken", large: false),
            FakeKestrel.Product("K-4", "LAMPS", "12.5", active: "N", nameEn: "Lamp"),
            FakeKestrel.Product("K-5", "TENTS", "299,00", nameEn: "Tent"));
        _kestrel.LastModified = "Thu, 19 Nov 2026 09:00:00 GMT";
        _harbor.Set(
            FakeHarbor.Item("HSC-1", "BAGS", 4999, "LIMITED", onHand: 3, colour: "Olive"),
            FakeHarbor.Item("HSC-3", "TEES", null, "AVAILABLE"),
            FakeHarbor.Item("HSC-5", "CAMP", 9900, "SOLD_OUT", discontinued: true));
        await _app.RequestRefreshAsync(Kestrel);
        await _app.RequestRefreshAsync(Harbor);

        var kestrelRun = await _app.Job(Kestrel).RunOnceAsync(Ct);
        var harborRun = await _app.Job(Harbor).RunOnceAsync(Ct);

        kestrelRun.Catalogue.Should().BeEquivalentTo(new { Result = CatalogueRunResult.Applied, Unlisted = 1 });
        harborRun.Catalogue.Should().BeEquivalentTo(new { Result = CatalogueRunResult.Applied, Unlisted = 1 });
        var pack = (await _app.ProductsAsync(Kestrel))["K-6"];
        pack.IsListed.Should().BeFalse();
        pack.ToDto(new AvailabilityPolicy(), "").Should().BeEquivalentTo(new { Sellable = false, Price = new PriceDto(96.57m, "USD") },
            "it still resolves by id with its last price, but cannot be bought");
        (await _app.ProductsAsync(Harbor))["HSC-4"].Should().BeEquivalentTo(new { IsListed = false, IsSellable = false });
        (await _app.StateAsync(Kestrel)).LiveProductCount.Should().Be(3);
        (await _app.StateAsync(Harbor)).LiveProductCount.Should().Be(2);
    }

    [Fact]
    [Trait("BR", "BR-MONEY-01")]
    public async Task BR_MONEY_01_a_new_exchange_rate_reprices_unchanged_kestrel_items_on_the_next_sync()
    {
        await SyncBothAsync();
        (await _app.ProductsAsync(Kestrel))["K-1"].PriceUsd.Should().Be(1355.17m);
        _kestrel.ClearCalls();

        // The rate is configuration: a changed Fx:EurToUsd takes effect with the next start of the API.
        await using var restarted = PartnerTestHost.Create(database, _harbor, _kestrel, b => b.UseSetting("Fx:EurToUsd", "1.2000"));
        var run = await restarted.Job(Kestrel).RunOnceAsync(Ct);

        run.Catalogue.Should().BeEquivalentTo(new { Result = CatalogueRunResult.Applied, Updated = 4 });
        _kestrel.Calls.Should().Contain(c => c.Uri.AbsolutePath == "/feed/products.xml" && c.Header("If-Modified-Since") == null,
            "the feed has not changed, so a conditional request would get a 304 and re-price nothing");
        (await restarted.ProductsAsync(Kestrel))["K-1"].Should().BeEquivalentTo(new { PriceUsd = 1498.80m, FxRateToUsd = 1.2m, SourcePrice = 1249.00m });
    }
}
