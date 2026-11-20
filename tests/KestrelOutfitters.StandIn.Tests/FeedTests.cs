using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace KestrelOutfitters.StandIn.Tests;

/// <summary>Contract: GET /feed/products.xml shape, number format and seed content.</summary>
public sealed partial class FeedTests(KestrelStandInFactory factory) : IClassFixture<KestrelStandInFactory>
{
    private static readonly string[] MappedGroups = ["SOCKS", "JACKETS", "BOOTS", "PACKS", "TENTS", "LAMPS", "CAPS"];

    [Fact]
    public async Task Feed_is_utf8_xml_with_the_published_declaration_and_root()
    {
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/feed/products.xml", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/xml");
        body.Should().StartWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        var root = XDocument.Parse(body).Root!;
        root.Name.LocalName.Should().Be("Catalogue");
        root.Name.Namespace.Should().Be(XNamespace.None);
        root.Elements().Should().OnlyContain(e => e.Name == "Product");
    }

    [Fact]
    public async Task Generated_is_rendered_with_a_plus_one_hour_offset_and_matches_last_modified()
    {
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/feed/products.xml", TestContext.Current.CancellationToken);
        var root = XDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Root!;

        var generated = (string)root.Attribute("generated")!;
        generated.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\+01:00$");
        DateTimeOffset.Parse(generated, CultureInfo.InvariantCulture)
            .Should().Be(response.Content.Headers.LastModified!.Value);
    }

    [Fact]
    public async Task Sample_product_K_7731_matches_the_published_example()
    {
        var product = (await GetProductsAsync()).Single(p => (string)p.Attribute("ref")! == "K-7731");

        product.Elements("Name").Select(n => ((string)n.Attribute("lang")!, n.Value))
            .Should().Equal(("de", "Merino Wandersocken"), ("en", "Merino Trail Socks"));
        var desc = product.Elements("Desc").Single(d => (string)d.Attribute("lang")! == "en");
        desc.Nodes().Should().ContainSingle().Which.Should().BeOfType<XCData>();
        desc.Value.Should().Contain("<b>long days</b>");
        product.Element("Group")!.Value.Should().Be("SOCKS");
        product.Element("Price")!.Value.Should().Be("14,95");
        ((string)product.Element("Price")!.Attribute("currency")!).Should().Be("EUR");
        product.Element("Specs")!.Elements("Spec").Select(s => ((string)s.Attribute("key")!, s.Value))
            .Should().Equal(("Colour", "Charcoal"), ("Size", "M"), ("Material", "Merino wool"));
        product.Element("Active")!.Value.Should().Be("Y");
        product.Elements().Select(e => e.Name.LocalName).Distinct()
            .Should().Equal("Name", "Desc", "Group", "Price", "Pictures", "Specs", "Active");
    }

    [Fact]
    public async Task Every_product_uses_only_published_element_names_and_values()
    {
        var products = await GetProductsAsync();

        products.Should().AllSatisfy(product =>
        {
            ((string?)product.Attribute("ref")).Should().MatchRegex("^K-[0-9]+$");
            product.Elements("Name").Should().NotBeEmpty();
            product.Elements("Name").Concat(product.Elements("Desc"))
                .Select(e => (string?)e.Attribute("lang")).Should().OnlyContain(lang => lang == "de" || lang == "en");
            product.Elements("Desc").Should().OnlyContain(d => d.Nodes().All(n => n is XCData));
            product.Element("Group")!.Value.Should().MatchRegex("^[A-Z]+$");
            product.Element("Price")!.Value.Should().MatchRegex(@"^\d{1,3}(\.\d{3})*,\d{2}$");
            ((string?)product.Element("Price")!.Attribute("currency")).Should().Be("EUR");
            product.Element("Active")!.Value.Should().BeOneOf("Y", "N");
            product.Descendants("Pic").Should().OnlyContain(pic =>
                ((string?)pic.Attribute("size") == "thumb" || (string?)pic.Attribute("size") == "large")
                && pic.Value.StartsWith("http://localhost/img/", StringComparison.Ordinal));
            product.Descendants("Spec").Should().OnlyContain(spec => !string.IsNullOrEmpty((string?)spec.Attribute("key")));
            product.Elements().Select(e => e.Name.LocalName)
                .Should().BeSubsetOf(["Name", "Desc", "Group", "Price", "Pictures", "Specs", "Active"]);
        });
    }

    [Fact]
    public async Task Seed_meets_the_contract_minimums()
    {
        var products = await GetProductsAsync();

        products.Should().HaveCountGreaterThanOrEqualTo(30);
        var groups = products.Select(p => p.Element("Group")!.Value).ToHashSet();
        groups.Should().Contain(MappedGroups);
        groups.Except(MappedGroups).Should().NotBeEmpty("at least one product sits in a group Lumen does not map");
        products.Should().Contain(p => p.Element("Active")!.Value == "N");
        products.Should().Contain(p => p.Elements("Name").All(n => (string)n.Attribute("lang")! == "de"));
        products.Should().Contain(p => !p.Descendants("Pic").Any());
        products.Should().Contain(p => p.Descendants("Pic").Any()
            && p.Descendants("Pic").All(pic => (string)pic.Attribute("size")! == "thumb"));
        products.Select(p => ParseEuro(p.Element("Price")!.Value)).Should().Contain(price => price > 1000m);
    }

    [Fact]
    public async Task Some_descriptions_carry_unsafe_html_for_lumen_to_sanitise()
    {
        var descriptions = string.Join("\n", (await GetProductsAsync()).SelectMany(p => p.Elements("Desc")).Select(d => d.Value));

        descriptions.Should().Contain("<script").And.Contain("onerror=").And.Contain("javascript:").And.Contain("<iframe");
    }

    [Fact]
    public async Task Seed_stock_has_sold_out_with_restock_date_low_and_plentiful_rows()
    {
        using var client = factory.CreateAuthenticatedClient();
        var refs = (await GetProductsAsync()).Select(p => (string)p.Attribute("ref")!).ToList();

        var rows = new List<string[]>();
        foreach (var chunk in refs.Chunk(20))
        {
            var csv = await client.GetStringAsync($"/stock?refs={string.Join(',', chunk)}", TestContext.Current.CancellationToken);
            rows.AddRange(csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(line => line.Split(';')));
        }

        rows.Should().HaveCount(refs.Count, "inactive products are still returned by /stock");
        var units = rows.Select(r => (Units: int.Parse(r[1], CultureInfo.InvariantCulture), Restock: r[2])).ToList();
        units.Should().Contain(r => r.Units == 0 && r.Restock.Length == 10);
        units.Should().Contain(r => r.Units >= 1 && r.Units <= 5);
        units.Should().Contain(r => r.Units > 20);
    }

    [Fact]
    public async Task Picture_urls_point_at_the_stand_in_and_serve_png_images_behind_auth()
    {
        var product = (await GetProductsAsync()).Single(p => (string)p.Attribute("ref")! == "K-7731");
        var pictures = product.Descendants("Pic").ToDictionary(p => (string)p.Attribute("size")!, p => p.Value);
        pictures["large"].Should().Be("http://localhost/img/k7731_l.png");
        pictures["thumb"].Should().Be("http://localhost/img/k7731_t.png");

        using var client = factory.CreateAuthenticatedClient();
        using var image = await client.GetAsync(pictures["large"], TestContext.Current.CancellationToken);
        image.StatusCode.Should().Be(HttpStatusCode.OK);
        image.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await image.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken))
            .Take(8).Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);

        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.GetAsync(pictures["large"], TestContext.Current.CancellationToken);
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var missing = await client.GetAsync("/img/k9999_l.png", TestContext.Current.CancellationToken);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await missing.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    private async Task<List<XElement>> GetProductsAsync()
    {
        using var client = factory.CreateAuthenticatedClient();
        var body = await client.GetStringAsync("/feed/products.xml", TestContext.Current.CancellationToken);
        return XDocument.Parse(body).Root!.Elements("Product").ToList();
    }

    // Parsed independently of the stand-in's own formatter, so the test does not grade itself.
    private static decimal ParseEuro(string value) =>
        decimal.Parse(value.Replace(".", "", StringComparison.Ordinal).Replace(',', '.'), CultureInfo.InvariantCulture);
}

/// <summary>Contract: Last-Modified and If-Modified-Since on the feed.</summary>
public sealed class FeedConditionalRequestTests(KestrelStandInFactory factory) : IClassFixture<KestrelStandInFactory>
{
    [Fact]
    public async Task If_modified_since_last_modified_gets_304_with_no_body_until_the_catalogue_changes()
    {
        using var client = factory.CreateAuthenticatedClient();
        using var first = await client.GetAsync("/feed/products.xml", TestContext.Current.CancellationToken);
        var lastModified = first.Content.Headers.LastModified;
        lastModified.Should().NotBeNull();
        first.Content.Headers.TryGetValues("Last-Modified", out var raw).Should().BeTrue();
        raw!.Single().Should().EndWith(" GMT");

        using (var notModified = await GetFeedAsync(client, lastModified!.Value))
        {
            notModified.StatusCode.Should().Be(HttpStatusCode.NotModified);
            (await notModified.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        }

        using (var older = await GetFeedAsync(client, lastModified.Value.AddSeconds(-1)))
        {
            older.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await factory.RunCliAsync("set-stock", "K-7733", "0")).ExitCode.Should().Be(0);
        using (var afterStockChange = await GetFeedAsync(client, lastModified.Value))
        {
            afterStockChange.StatusCode.Should().Be(HttpStatusCode.NotModified, "stock is not part of the feed");
        }

        // Same fake second as the seed: the change must still produce a strictly later Last-Modified.
        (await factory.RunCliAsync("set-price", "K-7733", "26,90")).ExitCode.Should().Be(0);
        using var afterPriceChange = await GetFeedAsync(client, lastModified.Value);
        afterPriceChange.StatusCode.Should().Be(HttpStatusCode.OK);
        afterPriceChange.Content.Headers.LastModified.Should().BeAfter(lastModified.Value);
        (await afterPriceChange.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("26,90");
    }

    private static Task<HttpResponseMessage> GetFeedAsync(HttpClient client, DateTimeOffset ifModifiedSince)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/feed/products.xml");
        request.Headers.IfModifiedSince = ifModifiedSince;
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}

/// <summary>Contract: about 1 in 10 feed requests fail with 503 (here forced to always).</summary>
public sealed class FeedFailureTests(AlwaysFailingFeedFactory factory) : IClassFixture<AlwaysFailingFeedFactory>
{
    [Fact]
    public async Task Failing_feed_request_gets_503_with_an_empty_body_even_when_conditional()
    {
        using var client = factory.CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/feed/products.xml");
        request.Headers.IfModifiedSince = DateTimeOffset.UtcNow.AddYears(1);

        using var plain = await client.GetAsync("/feed/products.xml", TestContext.Current.CancellationToken);
        using var conditional = await client.SendAsync(request, TestContext.Current.CancellationToken);

        plain.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await plain.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        conditional.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Failures_do_not_touch_other_endpoints_or_unauthenticated_requests()
    {
        using var client = factory.CreateAuthenticatedClient();
        using var anonymous = factory.CreateClient();

        using var stock = await client.GetAsync("/stock?refs=K-7731", TestContext.Current.CancellationToken);
        using var unauthenticated = await anonymous.GetAsync("/feed/products.xml", TestContext.Current.CancellationToken);

        stock.StatusCode.Should().Be(HttpStatusCode.OK);
        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

/// <summary>Contract: the feed takes 2 to 4 seconds (here fixed at 3 s on a fake clock).</summary>
public sealed class FeedLatencyTests(SlowFeedFactory factory) : IClassFixture<SlowFeedFactory>
{
    [Fact]
    public async Task Feed_waits_on_the_clock_before_answering()
    {
        using var client = factory.CreateAuthenticatedClient();
        using (await factory.CreateClient().GetAsync("/feed/products.xml", TestContext.Current.CancellationToken))
        {
            // Warm-up (401, no delay), so the timed request below is not slowed by first-request start-up.
        }

        var pending = client.GetAsync("/feed/products.xml", TestContext.Current.CancellationToken);
        var advanced = TimeSpan.Zero;
        var realTime = Stopwatch.StartNew();
        while (!pending.IsCompleted && realTime.Elapsed < TimeSpan.FromSeconds(10))
        {
            // Keeps nudging the fake clock until the response arrives; the real wait is only a poll.
            factory.Time.Advance(TimeSpan.FromMilliseconds(100));
            advanced += TimeSpan.FromMilliseconds(100);
            await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken));
        }

        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        advanced.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(3));
    }
}
