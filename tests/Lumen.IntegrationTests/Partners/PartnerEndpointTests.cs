using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Lumen.Api.Catalog;
using Lumen.Api.Partners.Media;
using Lumen.Domain.Catalog;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lumen.IntegrationTests.Partners;

/// <summary>The admin partner endpoints and the image proxy.</summary>
[Collection(PartnerSyncCollection.Name)]
public sealed class PartnerEndpointTests(PartnerDatabase database) : IAsyncLifetime
{
    private const string AdminEmail = "admin@lumengoods.test";
    private const string AdminPassword = "Admin!2026";

    private readonly FakeHarbor _harbor = new();
    private readonly FakeKestrel _kestrel = new();
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), $"lumen-webroot-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _app = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_webRoot, "own-media"));
        await File.WriteAllBytesAsync(Path.Combine(_webRoot, "own-media", "test-mug.png"), FakePartner.Png, Ct);
        _app = PartnerTestHost.Create(database, _harbor, _kestrel, b =>
        {
            b.UseSetting(WebHostDefaults.WebRootKey, _webRoot);
            b.UseSetting("Partners:Kestrel:ImageOrigins:0", $"http://{FakeKestrel.CdnHost}");
        });
        await _app.ResetPartnerDataAsync();
        PartnerTestHost.SeedDefaults(_harbor, _kestrel);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.ResetPartnerDataAsync();
        await _app.DisposeAsync();
        Directory.Delete(_webRoot, recursive: true);
    }

    [Fact]
    public async Task Refresh_requires_an_admin_sets_the_flag_and_the_next_run_serves_it()
    {
        var anonymous = new ApiSession(_app.CreateClient());
        await anonymous.StartAsync();
        var customer = await _app.SignInAsync("customer@lumengoods.test", "Customer!2026");
        var admin = await _app.SignInAsync(AdminEmail, AdminPassword);

        (await anonymous.PostAsJsonAsync("/api/v1/admin/partners/harbor/refresh", new { })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await customer.PostAsJsonAsync("/api/v1/admin/partners/harbor/refresh", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PostAsJsonAsync("/api/v1/admin/partners/acme/refresh", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var accepted = await admin.PostAsJsonAsync("/api/v1/admin/partners/harbor/refresh", new { });
        var confirming = await admin.PostAsJsonAsync("/api/v1/admin/partners/kestrel/refresh?confirmRemovals=true", new { });

        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var requested = (await _app.StateAsync("harbor")).RefreshRequestedUtc;
        requested.Should().NotBeNull();
        (await _app.StateAsync("harbor")).RemovalsConfirmedUtc.Should().BeNull();
        confirming.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await _app.StateAsync("kestrel")).RemovalsConfirmedUtc.Should().NotBeNull();

        var run = await _app.Job("harbor").RunOnceAsync(Ct);
        run.Catalogue.Should().NotBeNull("a requested refresh runs at once, whatever the schedule");
        (await _app.StateAsync("harbor")).RefreshRequestedUtc.Should().BeNull();
    }

    [Fact]
    public async Task The_partners_endpoint_reports_each_partner_in_the_documented_shape()
    {
        await _app.Job("harbor").RunOnceAsync(Ct);
        _kestrel.Down = true;
        await _app.Job("kestrel").RunOnceAsync(Ct);
        var admin = await _app.SignInAsync(AdminEmail, AdminPassword);

        var response = await admin.GetAsync("/api/v1/admin/partners");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var partners = json.RootElement.EnumerateArray().ToList();
        partners.Select(p => p.GetProperty("partner").GetString()).Should().Equal("harbor", "kestrel");
        foreach (var partner in partners)
        {
            partner.EnumerateObject().Select(p => p.Name).Should().Equal(
                "partner", "name", "lastSuccessUtc", "lastAttemptUtc", "lastError", "liveProductCount", "refreshRequestedUtc", "heldRemovals",
                "recentErrors");
        }

        var harbor = partners[0];
        harbor.GetProperty("name").GetString().Should().Be("Harbor Supply Co.");
        harbor.GetProperty("liveProductCount").GetInt32().Should().Be(3);
        harbor.GetProperty("lastSuccessUtc").GetString().Should().EndWith("Z");
        harbor.GetProperty("lastError").ValueKind.Should().Be(JsonValueKind.Null);

        var kestrel = partners[1];
        kestrel.GetProperty("name").GetString().Should().Be("Kestrel Outfitters");
        kestrel.GetProperty("lastError").GetString().Should().Contain("503");
        var error = kestrel.GetProperty("recentErrors").EnumerateArray().Should().NotBeEmpty().And.Subject.First();
        error.EnumerateObject().Select(p => p.Name).Should().Equal("occurredUtc", "operation", "statusCode", "message");
        error.GetProperty("statusCode").GetInt32().Should().Be(503);
    }

    [Fact]
    public async Task A_partner_image_is_served_through_the_partner_client_with_its_credentials()
    {
        await _app.Job("harbor").RunOnceAsync(Ct);
        var tote = (await _app.ProductsAsync(ProductSources.Harbor))["HSC-1"];
        _harbor.ClearCalls();
        var browser = _app.CreateClient();

        var first = await browser.GetAsync($"/media/{tote.Id}/1", Ct);
        var again = await browser.GetAsync($"/media/{tote.Id}/1", Ct);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        first.Headers.CacheControl!.ToString().Should().Be("public, max-age=86400");
        (await first.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(FakePartner.Png);
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        _harbor.Calls.Should().ContainSingle("the second request is served from the cache")
            .Which.Should().Match<PartnerCall>(c => c.Uri.AbsoluteUri == "http://harbor.test/media/HSC-1/side.png" && c.Header("X-Harbor-Key") == FakeHarbor.Key);
    }

    [Fact]
    public async Task An_image_with_no_rate_limit_token_in_time_gets_a_503_at_once_and_nothing_is_cached()
    {
        // One image token a minute, spent on the first image, so the second finds none for the rest of the test.
        await using var app = _app.WithWebHostBuilder(b =>
        {
            b.UseSetting("Partners:Harbor:RateLimit:ImageBurst", "1");
            b.UseSetting("Partners:Harbor:RateLimit:ImageRequestsPerMinute", "1");
            b.UseSetting("Partners:Media:RateLimitWait", "00:00:00.200");
            // A fetch queued for a token used to hold the browser until this ran out.
            b.UseSetting("Partners:Media:ResponseTimeout", "00:01:00");
        });
        await app.Job("harbor").RunOnceAsync(Ct);
        var tote = (await app.ProductsAsync(ProductSources.Harbor))["HSC-1"];
        var browser = app.CreateClient();
        (await browser.GetAsync($"/media/{tote.Id}/0", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        _harbor.ClearCalls();

        var clock = Stopwatch.StartNew();
        var throttled = await browser.GetAsync($"/media/{tote.Id}/1", Ct);
        var elapsed = clock.Elapsed;
        var again = await browser.GetAsync($"/media/{tote.Id}/1", Ct);

        throttled.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        throttled.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        throttled.Headers.CacheControl!.NoStore.Should().BeTrue();
        throttled.Headers.RetryAfter!.Delta.Should().BeGreaterThan(TimeSpan.Zero);
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the request waits RateLimitWait (0.2 s) for a token, not ResponseTimeout (60 s)");
        again.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "a throttled answer is not cached like a partner failure (502)");
        _harbor.Calls.Should().BeEmpty("without a token nothing is sent to Harbor");
        app.Services.GetRequiredService<PartnerImageCache>().Get("http://harbor.test/media/HSC-1/side.png").Should().BeNull();
    }

    [Fact]
    public async Task An_image_url_outside_the_partner_origin_is_refused_without_calling_it()
    {
        var product = new Product
        {
            Source = ProductSources.Kestrel,
            SourceProductId = "K-SSRF",
            Name = "Planted",
            Category = ProductCategories.Home,
            Images = [new ProductImage("http://169.254.169.254/latest/meta-data/", true), new ProductImage("http://kestrel.test.evil.test/img/x.png", false)],
            FirstSeenUtc = DateTimeOffset.UtcNow,
            LastUpdatedUtc = DateTimeOffset.UtcNow,
        };
        await _app.WithDbAsync(async db =>
        {
            db.Products.Add(product);
            return await db.SaveChangesAsync(Ct);
        });
        var browser = _app.CreateClient();

        var metadata = await browser.GetAsync($"/media/{product.Id}/0", Ct);
        var lookalike = await browser.GetAsync($"/media/{product.Id}/1", Ct);
        var missing = await browser.GetAsync($"/media/{product.Id}/2", Ct);

        metadata.StatusCode.Should().Be(HttpStatusCode.NotFound);
        lookalike.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _kestrel.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_image_on_a_configured_image_origin_is_fetched_without_partner_credentials()
    {
        var product = new Product
        {
            Source = ProductSources.Kestrel,
            SourceProductId = "K-CDN",
            Name = "CDN Lamp",
            Category = ProductCategories.Home,
            Images = [new ProductImage($"http://{FakeKestrel.CdnHost}/img/k-cdn_l.png", true)],
            FirstSeenUtc = DateTimeOffset.UtcNow,
            LastUpdatedUtc = DateTimeOffset.UtcNow,
        };
        await _app.WithDbAsync(async db =>
        {
            db.Products.Add(product);
            return await db.SaveChangesAsync(Ct);
        });

        var response = await _app.CreateClient().GetAsync($"/media/{product.Id}/0", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(FakePartner.Png);
        _kestrel.Calls.Should().ContainSingle().Which.Should().Match<PartnerCall>(c => c.Uri.Host == FakeKestrel.CdnHost && c.Header("Authorization") == null);
    }

    [Fact]
    public async Task A_partner_that_cannot_deliver_the_image_gives_a_502_problem()
    {
        await _app.Job("kestrel").RunOnceAsync(Ct);
        var parka = (await _app.ProductsAsync(ProductSources.Kestrel))["K-1"];
        _kestrel.Down = true;

        var response = await _app.CreateClient().GetAsync($"/media/{parka.Id}/0", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task An_own_media_file_is_served_from_wwwroot()
    {
        var product = new Product
        {
            Source = ProductSources.Lumen,
            SourceProductId = $"LG-{Guid.NewGuid():N}",
            Name = "Test Mug",
            Category = ProductCategories.Home,
            PriceUsd = 12.00m,
            OnHand = 1,
            Images = [new ProductImage("own-media/test-mug.png", true), new ProductImage("own-media/../appsettings.json", false)],
            FirstSeenUtc = DateTimeOffset.UtcNow,
            LastUpdatedUtc = DateTimeOffset.UtcNow,
        };
        await _app.WithDbAsync(async db =>
        {
            db.Products.Add(product);
            return await db.SaveChangesAsync(Ct);
        });
        var browser = _app.CreateClient();

        try
        {
            var image = await browser.GetAsync($"/media/{product.Id}/0", Ct);
            var traversal = await browser.GetAsync($"/media/{product.Id}/1", Ct);

            image.StatusCode.Should().Be(HttpStatusCode.OK);
            image.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
            (await image.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(FakePartner.Png);
            traversal.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await _app.WithDbAsync(db => db.Products.Where(p => p.Id == product.Id).ExecuteDeleteAsync(Ct));
        }
    }
}
