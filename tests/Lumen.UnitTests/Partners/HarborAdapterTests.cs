using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Lumen.Api.Partners.Harbor;
using Lumen.Api.Partners.Media;
using Lumen.Api.Partners.Ports;
using Lumen.Domain.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.RateLimiting;
using Polly.Telemetry;
using static Lumen.UnitTests.Partners.PartnerTestKit;

namespace Lumen.UnitTests.Partners;

/// <summary>
/// The Harbor adapter against fixtures that follow the contract but differ from our stand-in wherever the contract is
/// silent: cursor format, page fullness, ordering, extra fields.
/// </summary>
public sealed class HarborAdapterTests
{
    private static JsonObject Item(string code, string dept = "BAGS", int? cents = 4999, string status = "AVAILABLE", int? onHand = null, Action<JsonObject>? tweak = null)
    {
        var inventory = new JsonObject { ["status"] = status };
        if (onHand is not null)
        {
            inventory["on_hand"] = onHand;
        }

        var item = new JsonObject
        {
            ["item_code"] = code,
            ["title"] = $"Item {code}",
            ["summary"] = "Plain text summary.",
            ["dept"] = dept,
            ["unit_price_cents"] = cents,
            ["currency_code"] = "USD",
            ["media"] = new JsonArray(
                new JsonObject { ["kind"] = "image", ["href"] = $"/media/{code}/front.jpg", ["primary"] = true },
                new JsonObject { ["kind"] = "image", ["href"] = $"/media/{code}/side.jpg", ["primary"] = false }),
            ["inventory"] = inventory,
            ["props"] = new JsonArray(new JsonObject { ["name"] = "colour", ["value"] = "Olive" }),
            ["discontinued"] = false,
            ["updated_at"] = "2026-10-02T09:15:00Z",
            ["an_extra_field_harbor_might_add"] = new JsonObject { ["ignored"] = true },
        };
        tweak?.Invoke(item);
        return item;
    }

    private static HttpResponseMessage Page(string? nextCursor, params JsonObject[] items) =>
        StubPartner.Json(new JsonObject { ["data"] = new JsonArray(items.Select(i => (JsonNode?)i).ToArray()), ["next_cursor"] = nextCursor }.ToJsonString());

    private static async Task<CatalogFetchResult> FetchAsync(StubPartner harbor, bool full = true, string? watermark = null)
    {
        await using var services = Services(harbor: harbor);
        return await services.GetRequiredService<HarborCatalogSource>().FetchCatalogAsync(new CatalogFetchRequest(full, watermark), Ct);
    }

    private static PartnerProductSnapshot Single(CatalogFetchResult result, string code) =>
        result.Products.Should().ContainSingle(p => p.SourceProductId == code).Subject;

    [Fact]
    public async Task Opaque_cursors_are_followed_verbatim_through_an_empty_middle_page_until_null()
    {
        const string oddCursor = "c/2+=?&x==";
        var harbor = new StubPartner(request => request.Query.TryGetValue("cursor", out var cursor)
            ? cursor.ToString() switch
            {
                oddCursor => Page("last one", []),
                "last one" => Page(null, Item("HSC-2")),
                _ => StubPartner.Status(HttpStatusCode.BadRequest),
            }
            : Page(oddCursor, Item("HSC-1")));

        var result = await FetchAsync(harbor);

        result.Kind.Should().Be(CatalogFetchKind.Complete);
        result.Products.Select(p => p.SourceProductId).Should().BeEquivalentTo(new[] { "HSC-1", "HSC-2" });
        harbor.Requests.Should().HaveCount(3);
        harbor.Requests[1].Query["cursor"].ToString().Should().Be(oddCursor);
        harbor.Requests.Should().AllSatisfy(r => r.Query["limit"].ToString().Should().Be("25"));
        harbor.Requests.Should().AllSatisfy(r => r.Header(HarborKeyHandler.HeaderName).Should().Be(HarborKey));
    }

    [Fact]
    public async Task An_incremental_pass_asks_for_changes_since_the_watermark_minus_the_overlap_on_every_page()
    {
        var harbor = new StubPartner(request => request.Query.ContainsKey("cursor")
            ? Page(null, Item("HSC-2", tweak: i => i["updated_at"] = "2026-11-20T10:30:00Z"))
            : Page("next", Item("HSC-1")));

        var result = await FetchAsync(harbor, full: false, watermark: "2026-11-20T10:00:00.0000000Z");

        result.Kind.Should().Be(CatalogFetchKind.Changes);
        harbor.Requests.Should().HaveCount(2).And.AllSatisfy(r => r.Query["updated_since"].ToString().Should().Be("2026-11-20T09:55:00Z"));
        result.Watermark.Should().Be("2026-11-20T10:30:00.0000000Z", "the newest updated_at seen becomes the next watermark");
    }

    [Fact]
    [Trait("BR", "BR-SYNC-01")]
    public async Task BR_SYNC_01_a_full_pass_sets_the_watermark_from_its_own_items_so_a_future_date_cannot_pin_it()
    {
        var harbor = new StubPartner(_ => Page(null,
            Item("HSC-1", tweak: i => i["updated_at"] = "2026-11-20T10:30:00Z"),
            Item("HSC-2", tweak: i => i["updated_at"] = "2026-11-19T08:00:00Z")));

        var full = await FetchAsync(harbor, full: true, watermark: "2099-01-01T00:00:00.0000000Z");
        var incrementalWithNothingNewer = await FetchAsync(new StubPartner(_ => Page(null)), full: false, watermark: "2026-11-20T10:30:00.0000000Z");

        full.Watermark.Should().Be("2026-11-20T10:30:00.0000000Z");
        harbor.Requests.Should().ContainSingle().Which.Query.ContainsKey("updated_since").Should().BeFalse();
        incrementalWithNothingNewer.Watermark.Should().Be("2026-11-20T10:30:00.0000000Z", "an empty incremental pass keeps the watermark");
    }

    [Fact]
    public async Task A_repeated_cursor_abandons_the_pass_instead_of_looping()
    {
        var harbor = new StubPartner(_ => Page("same", Item("HSC-1")));

        var act = () => FetchAsync(harbor);

        await act.Should().ThrowAsync<PartnerDataException>().WithMessage("*repeated*");
        harbor.Requests.Should().HaveCount(2);
    }

    [Fact]
    [Trait("BR", "BR-SYNC-02")]
    public async Task BR_SYNC_02_a_429_waits_for_Retry_After_and_then_succeeds()
    {
        var calls = 0;
        var harbor = new StubPartner(_ =>
        {
            if (Interlocked.Increment(ref calls) > 1)
            {
                return Page(null, Item("HSC-1"));
            }

            var limited = StubPartner.Json("""{"error":{"code":"RATE_LIMITED","message":"Slow down"}}""", HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return limited;
        });

        var clock = Stopwatch.StartNew();
        var result = await FetchAsync(harbor);

        result.Products.Should().ContainSingle();
        harbor.Requests.Should().HaveCount(2);
        clock.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900), "Retry-After: 1 must be honoured, not the 10 ms back-off");
    }

    [Fact]
    public async Task A_401_is_reported_with_Harbor_error_code_and_not_retried()
    {
        var harbor = new StubPartner(_ => StubPartner.Json("""{"error":{"code":"UNAUTHORIZED","message":"Bad key"}}""", HttpStatusCode.Unauthorized));

        var act = () => FetchAsync(harbor);

        var failure = (await act.Should().ThrowAsync<PartnerException>()).Which;
        failure.StatusCode.Should().Be(401);
        failure.Message.Should().Contain("UNAUTHORIZED");
        harbor.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Unmapped_departments_have_no_category_so_they_are_not_shown()
    {
        var result = await FetchAsync(new StubPartner(_ => Page(null, Item("HSC-1", dept: " bags "), Item("HSC-9", dept: "GARDEN"))));

        Single(result, "HSC-1").Category.Should().Be(ProductCategories.Bags, "codes are compared trimmed and case-insensitively");
        Single(result, "HSC-9").Category.Should().BeNull();
    }

    [Fact]
    public async Task Prop_names_become_lower_camelCase_English_attribute_keys()
    {
        var result = await FetchAsync(new StubPartner(_ => Page(null, Item("HSC-1", tweak: i => i["props"] = new JsonArray(
            new JsonObject { ["name"] = "colour", ["value"] = "Olive" },
            new JsonObject { ["name"] = "Fill Power", ["value"] = "800" },
            new JsonObject { ["name"] = "Colour", ["value"] = "Duplicate loses" })))));

        Single(result, "HSC-1").Attributes.Should().BeEquivalentTo(new Dictionary<string, string> { ["color"] = "Olive", ["fillPower"] = "800" });
    }

    [Theory]
    [Trait("BR", "BR-CAT-01")]
    [InlineData("LIMITED", 8, AvailabilityStatus.InStock, 8)]
    [InlineData("LIMITED", 3, AvailabilityStatus.LowStock, 3)]
    [InlineData("LIMITED", null, AvailabilityStatus.LowStock, null)]
    [InlineData("AVAILABLE", null, AvailabilityStatus.InStock, null)]
    [InlineData("SOLD_OUT", null, AvailabilityStatus.OutOfStock, 0)]
    [InlineData("BACKORDER", null, AvailabilityStatus.OutOfStock, null)]
    public async Task BR_CAT_01_inventory_maps_to_availability_from_the_quantity_when_known(
        string status, int? onHand, AvailabilityStatus expectedStatus, int? expectedQuantity)
    {
        var result = await FetchAsync(new StubPartner(_ => Page(null, Item("HSC-1", status: status, onHand: onHand))));

        var stock = Single(result, "HSC-1").Stock!;
        new AvailabilityPolicy().Describe(stock.Quantity, stock.StatusWhenQuantityUnknown)
            .Should().Be(new Availability(expectedStatus, expectedQuantity));
    }

    [Fact]
    [Trait("BR", "BR-CAT-02")]
    public async Task BR_CAT_02_null_or_invalid_prices_and_discontinued_items_are_not_sellable()
    {
        var result = await FetchAsync(new StubPartner(_ => Page(null,
            Item("HSC-1", cents: null),
            Item("HSC-2", tweak: i => i["unit_price_cents"] = 49.5),
            Item("HSC-3", tweak: i => i["discontinued"] = true))));

        Single(result, "HSC-1").Price.Should().BeNull();
        Single(result, "HSC-1").Problems.Should().BeEmpty("price on request is a normal state, not a data error");
        Single(result, "HSC-2").Price.Should().BeNull();
        Single(result, "HSC-2").Problems.Should().ContainSingle(p => p.Contains("unit_price_cents"));
        Single(result, "HSC-3").IsActive.Should().BeFalse();
        Single(result, "HSC-3").Price.Should().Be(49.99m);
    }

    [Fact]
    public async Task Media_hrefs_resolve_against_the_base_url_with_exactly_one_primary_image()
    {
        var result = await FetchAsync(new StubPartner(_ => Page(null,
            Item("HSC-1"),
            Item("HSC-2", tweak: i => i["media"] = new JsonArray(
                new JsonObject { ["kind"] = "video", ["href"] = "/media/HSC-2/clip.mp4", ["primary"] = true },
                new JsonObject { ["kind"] = "image", ["href"] = "media/HSC-2/a.png" },
                new JsonObject { ["kind"] = "image", ["href"] = "/media/HSC-2/b.png" })))));

        Single(result, "HSC-1").Images.Should().Equal(
            new Lumen.Api.Catalog.ProductImage("http://harbor.test/media/HSC-1/front.jpg", true),
            new Lumen.Api.Catalog.ProductImage("http://harbor.test/media/HSC-1/side.jpg", false));
        Single(result, "HSC-2").Images.Should().Equal(
            new Lumen.Api.Catalog.ProductImage("http://harbor.test/media/HSC-2/a.png", true),
            new Lumen.Api.Catalog.ProductImage("http://harbor.test/media/HSC-2/b.png", false));
    }

    [Fact]
    public async Task A_malformed_item_is_skipped_and_reported_while_the_rest_of_the_page_is_used()
    {
        var result = await FetchAsync(new StubPartner(_ => Page(null,
            Item("HSC-1", tweak: i => i["title"] = 12345),
            Item("HSC-2", tweak: i => i.Remove("title")),
            Item("HSC-3"))));

        result.Products.Select(p => p.SourceProductId).Should().Equal("HSC-3");
        result.Problems.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_item_summary_is_escaped_into_safe_html()
    {
        var result = await FetchAsync(new StubPartner(_ => Page(null,
            Item("HSC-1", tweak: i => i["summary"] = "Fish & chips <img src=x onerror=alert(1)>"))));

        Single(result, "HSC-1").Description.Should().Be("Fish &amp; chips");
    }

    [Fact]
    public async Task Image_fetches_draw_on_their_own_smaller_share_so_they_cannot_starve_the_sync()
    {
        var harbor = new StubPartner(_ => StubPartner.Status(HttpStatusCode.OK));
        await using var services = Services(harbor: harbor);
        var gate = services.GetRequiredService<HarborRateGate>();
        using var images = services.GetRequiredService<IHttpClientFactory>().CreateClient(HarborCatalogSource.ImageHttpClientName);

        for (var i = 0; i < 5; i++)
        {
            (await images.GetAsync($"http://harbor.test/media/HSC-{i}/front.png", Ct)).Dispose();
        }

        gate.ImageLimiter.GetStatistics()!.CurrentAvailablePermits.Should().Be(0, "the image burst is spent");
        gate.Limiter.GetStatistics()!.CurrentAvailablePermits.Should().Be(5, "sync and order calls keep their whole burst");
        harbor.Requests.Should().HaveCount(5).And.AllSatisfy(r => r.Header(HarborKeyHandler.HeaderName).Should().Be(HarborKey));
    }

    [Fact]
    public async Task An_image_request_without_a_token_within_its_wait_is_not_sent_and_not_logged_as_an_error()
    {
        var harbor = new StubPartner(_ => StubPartner.Status(HttpStatusCode.OK));
        await using var services = Services(harbor: harbor, overrides: new()
        {
            ["Partners:Harbor:RateLimit:ImageBurst"] = "1",
            ["Partners:Harbor:RateLimit:ImageRequestsPerMinute"] = "1",
        });
        var logs = new LogLevels();
        services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        using var images = services.GetRequiredService<IHttpClientFactory>().CreateClient(HarborCatalogSource.ImageHttpClientName);
        (await images.GetAsync("http://harbor.test/media/HSC-1/front.png", Ct)).Dispose();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://harbor.test/media/HSC-2/front.png");
        request.Options.Set(ImageTokenPolicy.Key, new ImageTokenPolicy(TimeSpan.Zero));

        var act = () => images.SendAsync(request, Ct);

        (await act.Should().ThrowAsync<RateLimiterRejectedException>()).Which.RetryAfter.Should().BeGreaterThan(TimeSpan.Zero);
        harbor.Requests.Should().ContainSingle("the second image had no token and was not sent");
        logs.Levels.Should().NotContain(level => level >= LogLevel.Warning, "a refused image token is flow control, not a failure");
        var severity = services.GetRequiredService<IOptions<TelemetryOptions>>().Value.SeverityProvider!;
        severity(new SeverityProviderArguments(
                new ResilienceTelemetrySource($"{HarborCatalogSource.HttpClientName}-{HarborCatalogSource.HttpClientName}-pipeline", null, "RateLimiter"),
                new ResilienceEvent(ResilienceEventSeverity.Error, "OnRateLimiterRejected"),
                Polly.ResilienceContextPool.Shared.Get(Ct)))
            .Should().Be(ResilienceEventSeverity.Error, "a full sync and order queue is still an error");
    }

    [Fact]
    public void Rate_limits_that_together_exceed_Harbor_60_requests_per_minute_are_refused_at_start()
    {
        using var services = Services(overrides: new() { ["Partners:Harbor:RateLimit:ImageRequestsPerMinute"] = "20" });

        var act = () => services.GetRequiredService<IOptions<HarborOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*more than Harbor's 60 requests per minute*");
    }

    [Fact]
    public async Task The_api_key_is_sent_only_to_Harbor_own_origin()
    {
        var inner = new StubPartner(_ => StubPartner.Status(HttpStatusCode.OK));
        await using var services = Services(harbor: inner);
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(HarborCatalogSource.HttpClientName);

        (await client.GetAsync("http://harbor.test/media/a.png", Ct)).Dispose();
        (await client.GetAsync("http://harbor.test:8443/media/a.png", Ct)).Dispose();
        using var foreign = new HttpRequestMessage(HttpMethod.Get, "http://elsewhere.test/steal");
        foreign.Headers.Add(HarborKeyHandler.HeaderName, "planted");
        (await client.SendAsync(foreign, Ct)).Dispose();

        inner.Requests[0].Header(HarborKeyHandler.HeaderName).Should().Be(HarborKey);
        inner.Requests[1].Header(HarborKeyHandler.HeaderName).Should().BeNull("another port is another origin");
        inner.Requests[2].Header(HarborKeyHandler.HeaderName).Should().BeNull();
    }
}

/// <summary>The levels of everything logged, for tests that care only how loud a component is.</summary>
internal sealed class LogLevels : ILoggerProvider, ILogger
{
    private readonly List<LogLevel> _levels = [];

    public IReadOnlyList<LogLevel> Levels
    {
        get
        {
            lock (_levels)
            {
                return [.. _levels];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_levels)
        {
            _levels.Add(logLevel);
        }
    }

    public void Dispose()
    {
    }
}
