using Lumen.Api.Catalog;
using Lumen.Api.Partners;
using Microsoft.Extensions.Configuration;
using Lumen.Api.Partners.Harbor;
using Lumen.Api.Partners.Kestrel;
using Lumen.Api.Partners.Ports;
using Lumen.Api.Partners.Sync;
using Lumen.Domain.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace Lumen.UnitTests.Partners;

/// <summary>The engine's pure rules: the mass-deletion guard, scheduling, the content hash and snapshot mapping.</summary>
public sealed class SyncPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [Trait("BR", "BR-SYNC-02")]
    [InlineData(40, 21, 19, true)]
    [InlineData(12, 10, 2, true)]
    [InlineData(4, 4, 0, true)]
    [InlineData(40, 20, 20, false)]
    [InlineData(100, 11, 89, false)]
    [InlineData(4, 3, 1, false)]
    [InlineData(9, 9, 1, false)]
    [InlineData(40, 0, 40, false)]
    [InlineData(0, 0, 0, false)]
    public void BR_SYNC_02_removals_are_held_back_when_the_catalogue_is_empty_or_at_least_10_and_more_than_half_go(
        int listedNow, int removals, int productsInCatalogue, bool held)
    {
        (MassDeletionGuard.Check(listedNow, removals, productsInCatalogue, maxRemovalFraction: 0.5, minRemovals: 10) is not null).Should().Be(held);
    }

    [Fact]
    [Trait("BR", "BR-SYNC-01")]
    public void BR_SYNC_01_the_shipped_intervals_show_every_partner_change_within_15_minutes()
    {
        var shipped = new ConfigurationBuilder().AddJsonFile(Path.Combine(RepoRoot(), "src", "Lumen.Api", "appsettings.json")).Build();
        var harbor = shipped.GetSection(HarborOptions.SectionName).Get<HarborOptions>()!;
        var kestrel = shipped.GetSection(KestrelOptions.SectionName).Get<KestrelOptions>()!;
        var tick = shipped.GetSection(JobsOptions.SectionName).Get<JobsOptions>()!.PollInterval;

        // A healthy run takes seconds; a minute per run is generous. Slower runs mean a partner outage (BR-SYNC-02).
        // Two runs count: the one that just missed the change and the one that picks it up.
        var runs = 2 * TimeSpan.FromMinutes(1);
        var budget = TimeSpan.FromMinutes(15);

        // A change: the next incremental pass. A removal: only a full pass sees it, decided at the next due run.
        (harbor.CatalogueInterval + tick + runs).Should().BeLessThan(budget);
        (harbor.FullSyncInterval + harbor.CatalogueInterval + tick + runs).Should().BeLessThan(budget);

        // A feed change with a Last-Modified that never moves: only the unconditional fetch sees it. Stock: the stock
        // interval, possibly after a feed run in the same tick.
        (kestrel.FullSyncInterval + kestrel.CatalogueInterval + tick + runs).Should().BeLessThan(budget);
        (kestrel.StockInterval + tick + runs).Should().BeLessThan(budget);
        harbor.FullSyncInterval.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(12));
        kestrel.FullSyncInterval.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(15));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LumenGoods.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (LumenGoods.slnx).");
    }

    [Fact]
    [Trait("BR", "BR-SYNC-01")]
    public void BR_SYNC_01_harbor_runs_incrementally_every_minute_and_fully_every_10_minutes()
    {
        var options = new HarborOptions();
        var state = new PartnerSyncState
        {
            Partner = "harbor",
            Watermark = "2026-11-20T11:00:00.0000000Z",
            LastAttemptUtc = Now.AddSeconds(-30),
            LastFullSyncUtc = Now.AddMinutes(-5),
        };

        SyncSchedule.Catalogue(state, Now, options, mappingChanged: false, lastAttemptThisProcessUtc: Now.AddSeconds(-30)).Should().Be((false, false));

        state.LastAttemptUtc = Now.AddSeconds(-60);
        SyncSchedule.Catalogue(state, Now, options, false, Now.AddSeconds(-60)).Should().Be((true, false));

        state.LastFullSyncUtc = Now.AddMinutes(-10);
        SyncSchedule.Catalogue(state, Now, options, false, Now.AddSeconds(-60)).Should().Be((true, true));
    }

    [Fact]
    public void Refresh_now_and_a_mapping_change_force_a_full_run_but_a_failing_mapping_run_is_not_repeated_at_once()
    {
        var options = new KestrelOptions();
        var state = new PartnerSyncState { Partner = "kestrel", Watermark = "x", LastAttemptUtc = Now.AddSeconds(-10), LastFullSyncUtc = Now.AddSeconds(-10) };

        SyncSchedule.Catalogue(state, Now, options, mappingChanged: true, lastAttemptThisProcessUtc: null).Should().Be((true, true), "first run after a restart or rate change");
        SyncSchedule.Catalogue(state, Now, options, mappingChanged: true, lastAttemptThisProcessUtc: Now.AddSeconds(-10)).Should().Be((false, false));

        state.RefreshRequestedUtc = Now.AddSeconds(-1);
        SyncSchedule.Catalogue(state, Now, options, false, Now.AddSeconds(-10)).Should().Be((true, true));
        SyncSchedule.Stock(state, Now, options, catalogueApplied: false, lastStockAttemptUtc: Now).Should().BeTrue();
    }

    [Fact]
    [Trait("BR", "BR-SYNC-01")]
    public void BR_SYNC_01_kestrel_stock_is_read_after_a_feed_change_and_every_five_minutes_but_not_retried_every_tick()
    {
        var options = new KestrelOptions();
        var state = new PartnerSyncState { Partner = "kestrel", StockRefreshedUtc = Now.AddMinutes(-2) };

        SyncSchedule.Stock(state, Now, options, catalogueApplied: true, lastStockAttemptUtc: null).Should().BeTrue();
        SyncSchedule.Stock(state, Now, options, catalogueApplied: false, lastStockAttemptUtc: null).Should().BeFalse();

        state.StockRefreshedUtc = Now.AddMinutes(-6);
        SyncSchedule.Stock(state, Now, options, false, lastStockAttemptUtc: null).Should().BeTrue();
        SyncSchedule.Stock(state, Now, options, false, lastStockAttemptUtc: Now.AddSeconds(-5)).Should().BeFalse("the last attempt failed only seconds ago");
    }

    private static PartnerProductSnapshot Snapshot(string? category = ProductCategories.Apparel, decimal? price = 1249.00m, string currency = "EUR", bool active = true) =>
        new("K-1", "Parka", "<p>Warm</p>", category, "JACKETS", price, currency,
            [new ProductImage("http://kestrel.test/img/k1_l.png", true)], new Dictionary<string, string> { ["color"] = "Red" },
            active, Stock: null, UpdatedUtc: null, Problems: []);

    private static SnapshotMapper Mapper(string eurToUsd = "1.0850") =>
        PartnerTestKit.Services(overrides: new() { ["Fx:EurToUsd"] = eurToUsd }).GetRequiredService<SnapshotMapper>();

    [Fact]
    [Trait("BR", "BR-MONEY-01")]
    public void BR_MONEY_01_the_content_hash_covers_the_exchange_rate_so_a_rate_change_rewrites_unchanged_items()
    {
        var atOldRate = Mapper("1.0850").ToContent(Snapshot(), null, [])!;
        var atNewRate = Mapper("1.2000").ToContent(Snapshot(), null, [])!;

        atOldRate.PriceUsd.Should().Be(1355.17m);
        atNewRate.PriceUsd.Should().Be(1498.80m);
        atNewRate.ComputeHash().Should().NotBe(atOldRate.ComputeHash());
        Mapper("1.0850").ToContent(Snapshot(), null, [])!.ComputeHash().Should().Be(atOldRate.ComputeHash(), "same input, same hash");
    }

    [Fact]
    public void The_hash_of_a_stored_product_matches_the_hash_it_was_written_with()
    {
        var content = Mapper().ToContent(Snapshot(price: 49.9m, currency: "USD"), null, [])!;
        var product = new Product { Source = "kestrel", SourceProductId = "K-1", Name = "x", Category = "apparel" };
        content.ApplyTo(product);

        // As read back from numeric(12,2): 49.90 rather than 49.9.
        product.PriceUsd = 49.90m;
        product.SourcePrice = 49.90m;

        ProductContent.From(product).ComputeHash().Should().Be(product.ContentHash);
        product.OnHand.Should().BeNull("partner rows never carry own stock");
        product.Reserved.Should().Be(0);
    }

    [Fact]
    [Trait("BR", "BR-CAT-01")]
    public void BR_CAT_01_a_new_product_without_stock_information_is_unknown_and_an_existing_one_keeps_its_last_stock()
    {
        var mapper = Mapper();
        var fresh = mapper.ToContent(Snapshot(), null, [])!;
        fresh.Quantity.Should().BeNull();
        fresh.StatusWhenQuantityUnknown.Should().Be(AvailabilityStatus.OutOfStock);

        var existing = new Product { Source = "kestrel", SourceProductId = "K-1", Name = "Parka", Category = "apparel", Quantity = 4 };
        mapper.ToContent(Snapshot(), existing, [])!.Quantity.Should().Be(4);
    }

    [Fact]
    public void An_unmapped_category_is_never_stored_for_a_new_product_and_unlists_a_stored_one()
    {
        var mapper = Mapper();
        mapper.ToContent(Snapshot(category: null), null, []).Should().BeNull();

        var existing = new Product { Source = "kestrel", SourceProductId = "K-1", Name = "Parka", Category = "apparel" };
        var content = mapper.ToContent(Snapshot(category: null), existing, [])!;
        content.IsListed.Should().BeFalse();
        content.Category.Should().Be("apparel", "the last category is kept so carts and orders still resolve");
    }

    [Fact]
    [Trait("BR", "BR-CAT-02")]
    public void BR_CAT_02_an_unsupported_currency_or_zero_price_gives_no_price_and_a_problem()
    {
        var problems = new List<string>();
        Mapper().ToContent(Snapshot(price: 10m, currency: "GBP"), null, problems)!.PriceUsd.Should().BeNull();
        Mapper().ToContent(Snapshot(price: 0m), null, problems)!.PriceUsd.Should().BeNull();
        problems.Should().HaveCount(2);
    }
}
