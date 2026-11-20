using System.Net;
using System.Text.Json;
using static HarborSupply.StandIn.Tests.HttpTestExtensions;

namespace HarborSupply.StandIn.Tests;

/// <summary>Contract: POST /v2/orders. Each test orders different seeded items, so they can share one database.</summary>
public sealed class OrderTests(HarborFactory factory) : IClassFixture<HarborFactory>
{
    private static string NewRef() => "LG-" + Guid.NewGuid().ToString("N");

    private static int? OnHand(JsonElement item) =>
        item.GetProperty("inventory").TryGetProperty("on_hand", out var onHand) ? onHand.GetInt32() : null;

    [Fact]
    public async Task Accepted_order_returns_201_reduces_stock_and_bumps_updated_at()
    {
        using var client = factory.CreateKeyedClient();
        var before = await client.GetItemAsync("HSC-20012");

        using var response = await client.PostOrderAsync(NewRef(), ("HSC-20012", 2));
        var body = await response.Content.ReadAsStringAsync(Ct);
        var after = await client.GetItemAsync("HSC-20012");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        body.Should().MatchRegex("^\\{\"order_id\":\"H-\\d{5}\",\"status\":\"ACCEPTED\"\\}$");
        OnHand(after).Should().Be(OnHand(before) - 2);
        var now = factory.Clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        before.GetProperty("updated_at").GetString().Should().NotBe(now);
        after.GetProperty("updated_at").GetString().Should().Be(now);
    }

    [Fact]
    [Trait("CT", "CT-H-07")]
    public async Task CT_H_07_insufficient_stock_returns_409_with_item_code_and_available_and_orders_nothing()
    {
        using var client = factory.CreateKeyedClient();
        var externalRef = NewRef();
        var before = await client.GetItemAsync("HSC-30031");

        using var response = await client.PostOrderAsync(externalRef, ("HSC-30031", 7));
        var error = await response.ShouldBeHarborErrorAsync(409, "INSUFFICIENT_STOCK");
        var after = await client.GetItemAsync("HSC-30031");

        error.EnumerateObject().Select(p => p.Name).Should().Equal("code", "message", "item_code", "available");
        error.GetProperty("item_code").GetString().Should().Be("HSC-30031");
        error.GetProperty("available").GetInt32().Should().Be(6);
        after.GetRawText().Should().Be(before.GetRawText(), "nothing is ordered");

        // A 409 does not use up the reference: the same ref with a satisfiable line is a new order.
        using var retry = await client.PostOrderAsync(externalRef, ("HSC-30031", 1));
        retry.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    [Trait("CT", "CT-H-07")]
    public async Task CT_H_07_409_names_the_first_short_line_and_leaves_every_line_untouched()
    {
        using var client = factory.CreateKeyedClient();

        using var response = await client.PostOrderAsync(NewRef(), ("HSC-40042", 1), ("HSC-50055", 4), ("HSC-60062", 9));
        var error = await response.ShouldBeHarborErrorAsync(409, "INSUFFICIENT_STOCK");

        error.GetProperty("item_code").GetString().Should().Be("HSC-50055");
        error.GetProperty("available").GetInt32().Should().Be(3);
        OnHand(await client.GetItemAsync("HSC-40042")).Should().Be(8);
    }

    [Fact]
    public async Task Repeated_lines_for_one_item_are_checked_against_stock_together()
    {
        using var client = factory.CreateKeyedClient();

        using var response = await client.PostOrderAsync(NewRef(), ("HSC-60062", 3), ("HSC-60062", 3));
        var error = await response.ShouldBeHarborErrorAsync(409, "INSUFFICIENT_STOCK");

        error.GetProperty("item_code").GetString().Should().Be("HSC-60062");
        error.GetProperty("available").GetInt32().Should().Be(4);
    }

    [Fact]
    public async Task Concurrent_orders_for_the_last_units_never_oversell()
    {
        using var client = factory.CreateKeyedClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.PostOrderAsync(NewRef(), ("HSC-10023", 1))));
        var item = await client.GetItemAsync("HSC-10023");

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(7);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(5);
        item.GetProperty("inventory").GetRawText().Should().Be("{\"status\":\"SOLD_OUT\"}");
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Theory]
    [Trait("CT", "CT-H-08")]
    [InlineData("HSC-10028")] // discontinued
    [InlineData("HSC-10027")] // unit_price_cents is null
    [InlineData("HSC-99999")] // unknown
    public async Task CT_H_08_discontinued_unpriced_or_unknown_items_return_422_INVALID_ITEM(string itemCode)
    {
        using var client = factory.CreateKeyedClient();

        using var response = await client.PostOrderAsync(NewRef(), ("HSC-50053", 1), (itemCode, 1));

        var error = await response.ShouldBeHarborErrorAsync(422, "INVALID_ITEM");
        error.EnumerateObject().Select(p => p.Name).Should().Equal("code", "message");
    }

    [Fact]
    public async Task Precedence_is_400_then_422_then_409()
    {
        using var client = factory.CreateKeyedClient();

        using var invalidQty = await client.PostOrderAsync(NewRef(), ("HSC-99999", 1), ("HSC-50051", 0));
        using var invalidItem = await client.PostOrderAsync(NewRef(), ("HSC-50051", 500), ("HSC-10028", 1));

        await invalidQty.ShouldBeHarborErrorAsync(400, "INVALID_REQUEST");
        await invalidItem.ShouldBeHarborErrorAsync(422, "INVALID_ITEM");
    }

    [Fact]
    [Trait("CT", "CT-H-09")]
    public async Task CT_H_09_reused_external_ref_returns_200_with_the_original_order_and_orders_nothing()
    {
        using var client = factory.CreateKeyedClient();
        var externalRef = NewRef();

        using var first = await client.PostOrderAsync(externalRef, ("HSC-40046", 1));
        var firstBody = await first.Content.ReadAsStringAsync(Ct);
        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        using var replay = await client.PostOrderAsync(externalRef, ("HSC-40046", 1));
        using var replayWithOtherLines = await client.PostOrderAsync(externalRef, ("HSC-99999", 40));
        var item = await client.GetItemAsync("HSC-40046");

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        replayWithOtherLines.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync(Ct)).Should().Be(firstBody);
        (await replayWithOtherLines.Content.ReadAsStringAsync(Ct)).Should().Be(firstBody);
        OnHand(item).Should().Be(4, "stock is reduced once");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"external_ref":"r1","lines":[],"ship_to":{"name":"A","line1":"1 St","city":"C","region":"OR","postal_code":"1","country_iso2":"US"}}""")]
    [InlineData("""{"external_ref":"r2","lines":[{"item_code":"HSC-50053","qty":0}],"ship_to":{"name":"A","line1":"1 St","city":"C","region":"OR","postal_code":"1","country_iso2":"US"}}""")]
    [InlineData("""{"external_ref":"r3","lines":[{"item_code":"HSC-50053","qty":-2}],"ship_to":{"name":"A","line1":"1 St","city":"C","region":"OR","postal_code":"1","country_iso2":"US"}}""")]
    [InlineData("""{"external_ref":"r4","lines":[{"item_code":"HSC-50053","qty":1.5}],"ship_to":{"name":"A","line1":"1 St","city":"C","region":"OR","postal_code":"1","country_iso2":"US"}}""")]
    [InlineData("""{"external_ref":"r5","lines":[{"item_code":"HSC-50053","qty":1}]}""")]
    [InlineData("""{"external_ref":"r6","lines":[{"item_code":"HSC-50053","qty":1}],"ship_to":{"name":"A","line1":"1 St","region":"OR","postal_code":"1","country_iso2":"US"}}""")]
    [InlineData("""{"external_ref":"r7","lines":[{"item_code":"HSC-50053","qty":1}],"ship_to":{"name":"A","line1":"1 St","city":"C","region":"OR","postal_code":"1","country_iso2":"USA"}}""")]
    [InlineData("""{"lines":[{"item_code":"HSC-50053","qty":1}],"ship_to":{"name":"A","line1":"1 St","city":"C","region":"OR","postal_code":"1","country_iso2":"US"}}""")]
    public async Task Malformed_order_requests_return_400_INVALID_REQUEST(string json)
    {
        using var client = factory.CreateKeyedClient();

        using var response = await client.PostRawOrderAsync(json);

        await response.ShouldBeHarborErrorAsync(400, "INVALID_REQUEST");
    }

    [Fact]
    public async Task Line2_may_be_null_or_omitted()
    {
        using var client = factory.CreateKeyedClient();
        var json = """{"external_ref":"%REF%","lines":[{"item_code":"HSC-50053","qty":1}],"ship_to":{"name":"Ana Silva","line1":"12 Harbour Road","city":"Portland","region":"OR","postal_code":"97201","country_iso2":"US"}}""";

        using var response = await client.PostRawOrderAsync(json.Replace("%REF%", NewRef(), StringComparison.Ordinal));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
