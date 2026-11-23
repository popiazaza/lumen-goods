using System.Globalization;
using System.Net;
using System.Text;
using Lumen.Api.Catalog;
using Lumen.Api.Partners.Kestrel;
using Lumen.Api.Partners.Mapping;
using Lumen.Api.Partners.Ports;
using Lumen.Api.Partners.Sync;
using Lumen.Domain.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;
using static Lumen.UnitTests.Partners.PartnerTestKit;

namespace Lumen.UnitTests.Partners;

/// <summary>The Kestrel adapter (feed and stock) against contract-valid but hostile fixtures.</summary>
public sealed class KestrelAdapterTests
{
    private const string LastModified = "Wed, 18 Nov 2026 09:00:00 GMT";

    private const string Feed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Catalogue generated="2026-11-01T06:00:00+01:00">
          <Product ref="K-7731">
            <Name lang="de">Merino Wandersocken</Name>
            <Name lang="en">Merino Trail Socks</Name>
            <Desc lang="en"><![CDATA[<p>Cushioned merino socks for <b>long days</b> on the trail.</p><script>alert(1)</script><img src=x onerror="alert(2)"><a href="javascript:alert(3)">details</a><iframe src="https://evil.test"></iframe><p style="color:red" onclick="x()">Styled</p>]]></Desc>
            <Group>SOCKS</Group>
            <Price currency="EUR">14,95</Price>
            <Pictures>
              <Pic size="thumb">http://kestrel.test/img/k7731_t.png</Pic>
              <Pic size="large">http://kestrel.test/img/k7731_l.png</Pic>
            </Pictures>
            <Specs>
              <Spec key="Colour">Charcoal</Spec>
              <Spec key="Waterproof rating">28.000 mm</Spec>
            </Specs>
            <Active>Y</Active>
          </Product>
          <Product ref="K-7745">
            <Name lang="de">Expeditionsdaunenparka</Name>
            <Desc lang="de"><![CDATA[<p>Für Expeditionen bis &minus;50&nbsp;°C.</p>]]></Desc>
            <Group> jackets </Group>
            <Price currency="EUR">1.249,00</Price>
            <Pictures><Pic size="thumb">http://kestrel.test/img/k7745_t.png</Pic></Pictures>
            <Specs/>
            <Active>Y</Active>
          </Product>
          <Product ref="K-7790">
            <Name lang="en">Leather Gloves</Name>
            <Group>GLOVES</Group>
            <Price currency="EUR">39,00</Price>
            <Specs/>
            <Active>N</Active>
          </Product>
          <Product ref="K-7791">
            <Name lang="en">Odd Price Lamp</Name>
            <Group>LAMPS</Group>
            <Price currency="EUR">12.5</Price>
            <Specs/>
            <Active>Y</Active>
          </Product>
        </Catalogue>
        """;

    private static HttpResponseMessage FeedResponse(string xml)
    {
        var response = StubPartner.Text(xml, "application/xml");
        response.Content.Headers.TryAddWithoutValidation("Last-Modified", LastModified);
        return response;
    }

    private static async Task<CatalogFetchResult> FetchAsync(StubPartner kestrel, bool full = true, string? watermark = null)
    {
        await using var services = Services(kestrel: kestrel);
        return await services.GetRequiredService<KestrelCatalogSource>().FetchCatalogAsync(new CatalogFetchRequest(full, watermark), Ct);
    }

    private static PartnerProductSnapshot Single(CatalogFetchResult result, string reference) =>
        result.Products.Should().ContainSingle(p => p.SourceProductId == reference).Subject;

    [Fact]
    public async Task The_feed_maps_names_by_language_pictures_by_size_groups_by_the_map_and_converts_generated_to_utc()
    {
        var result = await FetchAsync(new StubPartner(_ => FeedResponse(Feed)));

        result.Kind.Should().Be(CatalogFetchKind.Complete);
        result.Watermark.Should().Be(LastModified);
        result.ChangedAtUtc.Should().Be(new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero));

        var socks = Single(result, "K-7731");
        socks.Name.Should().Be("Merino Trail Socks", "English wins even though German comes first");
        socks.Category.Should().Be(ProductCategories.Apparel);
        socks.Images.Should().Equal(new ProductImage("http://kestrel.test/img/k7731_l.png", true));
        socks.Attributes.Should().BeEquivalentTo(new Dictionary<string, string> { ["color"] = "Charcoal", ["waterproofRating"] = "28.000 mm" });
        socks.Currency.Should().Be("EUR");
        socks.Stock.Should().BeNull("the feed carries no stock");

        var parka = Single(result, "K-7745");
        parka.Name.Should().Be("Expeditionsdaunenparka", "a German-only product keeps its German name");
        parka.Description.Should().Contain("Expeditionen");
        parka.Images.Should().Equal(new ProductImage("http://kestrel.test/img/k7745_t.png", true));

        Single(result, "K-7790").Category.Should().BeNull("GLOVES is not in the mapping table");
        Single(result, "K-7790").IsActive.Should().BeFalse();
        Single(result, "K-7790").Images.Should().BeEmpty();
    }

    [Fact]
    public async Task Description_html_is_sanitised_to_the_allow_list()
    {
        var description = Single(await FetchAsync(new StubPartner(_ => FeedResponse(Feed))), "K-7731").Description;

        description.Should().StartWith("<p>Cushioned merino socks for <b>long days</b> on the trail.</p>");
        description.Should().Contain("details").And.Contain("<p>Styled</p>");
        foreach (var unsafePart in new[] { "<script", "alert", "onerror", "javascript:", "<iframe", "<a", "<img", "style=", "onclick" })
        {
            description.Should().NotContain(unsafePart);
        }
    }

    [Theory]
    [InlineData("14,95", "14.95")]
    [InlineData("1.249,00", "1249.00")]
    [InlineData("12.345.678,90", "12345678.90")]
    [InlineData("1249,00", "1249.00")]
    [InlineData("12345678,90", "12345678.90")]
    [InlineData(" 0,99 ", "0.99")]
    public void Kestrel_prices_parse_with_comma_decimals_and_dot_thousands(string text, string expected)
    {
        KestrelPrice.TryParse(text, out var amount).Should().BeTrue();
        amount.Should().Be(decimal.Parse(expected, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("12.5")]
    [InlineData("1,249.00")]
    [InlineData("1249.00")]
    [InlineData("1.24,00")]
    [InlineData("12.3456,00")]
    [InlineData("1249,0")]
    [InlineData("14,9")]
    [InlineData("14,950")]
    [InlineData("-14,95")]
    [InlineData("14")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("١٤,٩٥")]
    public void Anything_else_is_not_a_kestrel_price(string text) => KestrelPrice.TryParse(text, out _).Should().BeFalse();

    [Fact]
    [Trait("BR", "BR-CAT-02")]
    public async Task BR_CAT_02_an_invalid_price_gives_no_price_and_a_recorded_problem()
    {
        var lamp = Single(await FetchAsync(new StubPartner(_ => FeedResponse(Feed))), "K-7791");

        lamp.Price.Should().BeNull();
        lamp.Problems.Should().ContainSingle(p => p.Contains("12.5"));
    }

    [Theory]
    [Trait("BR", "BR-MONEY-01")]
    [InlineData("1249.00", "1355.17")]
    [InlineData("14.95", "16.22")]
    public void BR_MONEY_01_eur_prices_convert_per_unit_rounding_half_away_from_zero(string eurText, string usdText)
    {
        var (eur, usd) = (decimal.Parse(eurText, CultureInfo.InvariantCulture), decimal.Parse(usdText, CultureInfo.InvariantCulture));
        using var services = Services();
        var price = services.GetRequiredService<CurrencyConverter>().ToUsd(eur, "EUR");

        price.Should().Be(new UsdPrice(usd, eur, "EUR", 1.0850m, null));
    }

    [Fact]
    public async Task A_304_is_not_modified_and_the_stored_Last_Modified_is_echoed_verbatim()
    {
        var kestrel = new StubPartner(request => request.Header("If-Modified-Since") == LastModified
            ? StubPartner.Status(HttpStatusCode.NotModified)
            : FeedResponse(Feed));

        var result = await FetchAsync(kestrel, full: false, watermark: LastModified);

        result.Kind.Should().Be(CatalogFetchKind.NotModified);
        result.Products.Should().BeEmpty();
        result.Watermark.Should().Be(LastModified);
    }

    [Fact]
    public async Task A_full_fetch_ignores_the_watermark()
    {
        var kestrel = new StubPartner(_ => FeedResponse(Feed));

        await FetchAsync(kestrel, full: true, watermark: LastModified);

        kestrel.Requests.Should().ContainSingle().Which.Header("If-Modified-Since").Should().BeNull();
    }

    [Fact]
    [Trait("BR", "BR-SYNC-02")]
    public async Task BR_SYNC_02_503s_are_retried_and_a_feed_that_stays_down_is_reported()
    {
        var kestrel = new StubPartner(_ => StubPartner.Status(HttpStatusCode.ServiceUnavailable));

        var act = () => FetchAsync(kestrel);

        (await act.Should().ThrowAsync<PartnerException>()).Which.StatusCode.Should().Be(503);
        kestrel.Requests.Should().HaveCount(4, "one attempt plus three retries");
    }

    [Fact]
    [Trait("BR", "BR-SYNC-02")]
    public async Task BR_SYNC_02_an_occasional_503_is_absorbed_by_a_retry_in_the_same_run()
    {
        var calls = 0;
        var kestrel = new StubPartner(_ => Interlocked.Increment(ref calls) == 1 ? StubPartner.Status(HttpStatusCode.ServiceUnavailable) : FeedResponse(Feed));

        var result = await FetchAsync(kestrel);

        result.Products.Should().HaveCount(4);
    }

    [Fact]
    public async Task A_feed_with_a_DTD_is_refused()
    {
        const string withDtd = """
            <?xml version="1.0"?>
            <!DOCTYPE Catalogue [<!ENTITY lol "lol"><!ENTITY lol2 "&lol;&lol;&lol;&lol;">]>
            <Catalogue generated="2026-11-01T06:00:00+01:00"><Product ref="K-1"><Name lang="en">&lol2;</Name><Active>Y</Active></Product></Catalogue>
            """;

        var act = () => FetchAsync(new StubPartner(_ => FeedResponse(withDtd)));

        await act.Should().ThrowAsync<PartnerDataException>().WithMessage("*not a complete, safe XML document*");
    }

    [Fact]
    public async Task A_truncated_feed_is_rejected_as_a_whole()
    {
        var truncated = Feed[..Feed.IndexOf("<Product ref=\"K-7790\">", StringComparison.Ordinal)];

        var act = () => FetchAsync(new StubPartner(_ => FeedResponse(truncated)));

        await act.Should().ThrowAsync<PartnerDataException>();
    }

    [Fact]
    public async Task Stock_for_45_refs_takes_three_requests_of_at_most_20()
    {
        var kestrel = new StubPartner(request =>
        {
            var refs = request.Query["refs"].ToString().Split(',');
            var csv = new StringBuilder("ref;units;restock_date\r\n");
            foreach (var reference in refs)
            {
                csv.Append(reference).Append(";5;\r\n");
            }

            return StubPartner.Text(csv.ToString(), "text/csv");
        });
        await using var services = Services(kestrel: kestrel);
        var references = Enumerable.Range(1, 45).Select(i => $"K-{i}").ToList();

        var result = await services.GetRequiredService<KestrelStockSource>().FetchStockAsync(references, Ct);

        kestrel.Requests.Should().HaveCount(3);
        kestrel.Requests.Select(r => r.Query["refs"].ToString().Split(',').Length).Should().Equal(20, 20, 5);
        kestrel.Requests.SelectMany(r => r.Query["refs"].ToString().Split(',')).Should().BeEquivalentTo(references);
        result.Levels.Should().HaveCount(45).And.AllSatisfy(level => level.Value.Should().Be(StockLevel.Exactly(5)));
    }

    [Fact]
    [Trait("BR", "BR-CAT-01")]
    public async Task BR_CAT_01_a_ref_missing_from_the_stock_csv_is_unknown_and_shown_out_of_stock()
    {
        // BOM, reordered columns, quoting, a blank line and a charset parameter: all within "CSV with a header row".
        var kestrel = new StubPartner(_ => StubPartner.Text("﻿units;\"ref\";restock_date\r\n\r\n3;K-1;\r\n\"0\";\"K-2\";2026-12-01\r\n", "text/csv"));
        await using var services = Services(kestrel: kestrel);

        var result = await services.GetRequiredService<KestrelStockSource>().FetchStockAsync(["K-1", "K-2", "K-3"], Ct);

        result.Levels["K-1"].Should().Be(StockLevel.Exactly(3));
        result.Levels["K-2"].Should().Be(StockLevel.Exactly(0));
        result.Levels["K-3"].Should().Be(StockLevel.Unknown);
        new AvailabilityPolicy().Describe(result.Levels["K-3"].Quantity, result.Levels["K-3"].StatusWhenQuantityUnknown)
            .Should().Be(new Availability(AvailabilityStatus.OutOfStock, null));
    }

    [Theory]
    [Trait("BR", "BR-CAT-01")]
    [InlineData("twelve", null)]
    [InlineData("1.5", null)]
    [InlineData("1,5", null)]
    [InlineData("", null)]
    [InlineData("-3", 0)]
    public async Task BR_CAT_01_units_that_are_not_a_whole_number_or_negative_are_never_in_stock_and_are_recorded(string units, int? quantity)
    {
        var kestrel = new StubPartner(_ => StubPartner.Text($"ref;units;restock_date\r\nK-1;{units};\r\n", "text/csv"));
        await using var services = Services(kestrel: kestrel);

        var result = await services.GetRequiredService<KestrelStockSource>().FetchStockAsync(["K-1"], Ct);

        result.Levels["K-1"].Quantity.Should().Be(quantity);
        new AvailabilityPolicy().Describe(result.Levels["K-1"].Quantity, result.Levels["K-1"].StatusWhenQuantityUnknown).Status
            .Should().Be(AvailabilityStatus.OutOfStock);
        result.Problems.Should().ContainSingle().Which.Should().Contain("K-1");
    }

    [Theory]
    [Trait("BR", "BR-CAT-01")]
    [InlineData("")]
    [InlineData("\r\n")]
    [InlineData("K-1;5;\r\n")]
    [InlineData("reference;stock\r\nK-1;5\r\n")]
    public async Task BR_CAT_01_a_stock_csv_without_a_ref_units_header_is_refused_and_reads_no_stock(string csv)
    {
        var kestrel = new StubPartner(_ => StubPartner.Text(csv, "text/csv"));
        await using var services = Services(kestrel: kestrel);

        var act = () => services.GetRequiredService<KestrelStockSource>().FetchStockAsync(["K-1"], Ct);

        // The only chunk failed, so the whole read fails and the sync keeps the stock as last read.
        (await act.Should().ThrowAsync<PartnerException>().WithMessage("Kestrel stock could not be read: Kestrel stock CSV*"))
            .WithInnerException<PartnerDataException>();
    }

    [Fact]
    public async Task Failing_images_open_only_the_image_breaker_so_feed_stock_and_orders_keep_going()
    {
        var kestrel = new StubPartner(r => r.Uri.AbsolutePath.StartsWith("/img/", StringComparison.Ordinal)
            ? StubPartner.Status(HttpStatusCode.InternalServerError)
            : StubPartner.Status(HttpStatusCode.OK));
        await using var services = Services(kestrel: kestrel);
        var registration = services.GetRequiredService<PartnerRegistry>().Get(ProductSources.Kestrel);
        var clients = services.GetRequiredService<IHttpClientFactory>();
        using var images = clients.CreateClient(registration.ImageHttpClientName);
        using var main = clients.CreateClient(registration.HttpClientName);

        var opened = false;
        for (var i = 0; i < 10 && !opened; i++)
        {
            try
            {
                (await images.GetAsync($"http://kestrel.test/img/{i}.png", Ct)).Dispose();
            }
            catch (BrokenCircuitException)
            {
                opened = true;
            }
        }

        opened.Should().BeTrue("enough failing image requests open the image client's breaker");
        using var feed = await main.GetAsync("http://kestrel.test/feed/products.xml", Ct);
        feed.StatusCode.Should().Be(HttpStatusCode.OK, "the breaker that the feed, stock, live checks and orders use is still closed");
        kestrel.Requests.Should().AllSatisfy(r => r.Header("Authorization").Should().StartWith("Basic "), "images carry Kestrel's credentials too");
    }

    [Fact]
    public async Task Basic_credentials_go_only_to_Kestrel_own_origin()
    {
        var inner = new StubPartner(_ => StubPartner.Status(HttpStatusCode.OK));
        await using var services = Services(kestrel: inner);
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(KestrelCatalogSource.HttpClientName);

        (await client.GetAsync("http://kestrel.test/img/a.png", Ct)).Dispose();
        (await client.GetAsync("https://kestrel.example/img/a.png", Ct)).Dispose();

        inner.Requests[0].Header("Authorization").Should().Be("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("lumen:test-secret")));
        inner.Requests[1].Header("Authorization").Should().BeNull();
    }
}
