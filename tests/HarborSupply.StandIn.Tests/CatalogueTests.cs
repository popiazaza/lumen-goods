using System.Net;
using System.Text.Json;
using static HarborSupply.StandIn.Tests.HttpTestExtensions;

namespace HarborSupply.StandIn.Tests;

/// <summary>Contract: GET /v2/catalog/items and GET /v2/catalog/items/{item_code}. Read-only, so one factory.</summary>
public sealed class CatalogueTests(HarborFactory factory) : IClassFixture<HarborFactory>
{
    private static readonly string[] ItemFields =
        ["item_code", "title", "summary", "dept", "unit_price_cents", "currency_code", "media", "inventory", "props", "discontinued", "updated_at"];

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=26")]
    [InlineData("limit=-1")]
    [InlineData("limit=ten")]
    [InlineData("cursor=not-a-cursor")]
    [InlineData("updated_since=yesterday")]
    [InlineData("updated_since=2026-10-02T09:15:00")]
    public async Task Invalid_query_parameters_return_400_INVALID_REQUEST(string query)
    {
        using var client = factory.CreateKeyedClient();

        using var response = await client.GetAsync("/v2/catalog/items?" + query, Ct);

        await response.ShouldBeHarborErrorAsync(400, "INVALID_REQUEST");
    }

    [Fact]
    public async Task Default_limit_is_25_and_limits_1_and_25_are_accepted()
    {
        using var client = factory.CreateKeyedClient();

        var defaultPage = await (await client.GetAsync("/v2/catalog/items", Ct)).ReadJsonAsync();
        var onePage = await (await client.GetAsync("/v2/catalog/items?limit=1", Ct)).ReadJsonAsync();

        defaultPage.GetProperty("data").GetArrayLength().Should().Be(25);
        onePage.GetProperty("data").GetArrayLength().Should().Be(1);
        onePage.GetProperty("next_cursor").GetString().Should().NotBeNullOrEmpty();
    }

    [Theory]
    [Trait("CT", "CT-H-03")]
    [InlineData(25)]
    [InlineData(7)]
    [InlineData(1)]
    public async Task CT_H_03_paging_by_cursor_returns_every_item_exactly_once_and_ends_with_null_cursor(int limit)
    {
        using var client = factory.CreateKeyedClient();

        var (items, pages) = await client.GetAllPagesAsync("limit=" + limit);

        var codes = items.Select(i => i.GetProperty("item_code").GetString()).ToList();
        codes.Should().OnlyHaveUniqueItems();
        codes.Should().HaveCountGreaterThanOrEqualTo(40);
        pages[^1].Should().Contain("\"next_cursor\":null", "the property is present and null on the last page");
        pages.Take(pages.Count - 1).Should().AllSatisfy(p => p.Should().NotContain("\"next_cursor\":null"));
        pages.Should().AllSatisfy(p => p.Should().StartWith("{\"data\":["));
    }

    [Fact]
    [Trait("CT", "CT-H-05")]
    public async Task CT_H_05_item_shape_matches_the_contract_with_on_hand_only_when_LIMITED()
    {
        using var client = factory.CreateKeyedClient();

        var (items, _) = await client.GetAllPagesAsync();

        items.Should().AllSatisfy(item =>
        {
            item.EnumerateObject().Select(p => p.Name).Should().Equal(ItemFields);
            item.GetProperty("currency_code").GetString().Should().Be("USD");
            item.GetProperty("updated_at").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$");
            var inventory = item.GetProperty("inventory");
            var status = inventory.GetProperty("status").GetString();
            if (status == "LIMITED")
            {
                inventory.GetProperty("on_hand").GetInt32().Should().BeInRange(1, 10);
            }
            else
            {
                status.Should().BeOneOf("AVAILABLE", "SOLD_OUT");
                inventory.TryGetProperty("on_hand", out _).Should().BeFalse("on_hand is present only when the status is LIMITED");
            }
        });
        items.Should().Contain(i => i.GetProperty("unit_price_cents").ValueKind == JsonValueKind.Null, "null prices are written as null, not omitted");
    }

    [Fact]
    public async Task The_spec_sample_item_is_served_exactly()
    {
        using var client = factory.CreateKeyedClient();

        using var response = await client.GetAsync("/v2/catalog/items/HSC-10021", Ct);
        var json = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        json.Should().Be(
            "{\"item_code\":\"HSC-10021\",\"title\":\"Waxed Canvas Tote\",\"summary\":\"Heavy waxed canvas tote with leather handles.\"," +
            "\"dept\":\"BAGS\",\"unit_price_cents\":4999,\"currency_code\":\"USD\"," +
            "\"media\":[{\"kind\":\"image\",\"href\":\"/media/HSC-10021/front.png\",\"primary\":true}," +
            "{\"kind\":\"image\",\"href\":\"/media/HSC-10021/side.png\",\"primary\":false}]," +
            "\"inventory\":{\"status\":\"LIMITED\",\"on_hand\":3},\"props\":[{\"name\":\"colour\",\"value\":\"Olive\"}]," +
            "\"discontinued\":false,\"updated_at\":\"2026-10-02T09:15:00Z\"}");
    }

    [Fact]
    [Trait("CT", "CT-H-06")]
    public async Task CT_H_06_unknown_item_returns_404_ITEM_NOT_FOUND()
    {
        using var client = factory.CreateKeyedClient();

        using var response = await client.GetAsync("/v2/catalog/items/HSC-99999", Ct);

        await response.ShouldBeHarborErrorAsync(404, "ITEM_NOT_FOUND");
    }

    [Fact]
    [Trait("CT", "CT-H-04")]
    public async Task CT_H_04_updated_since_returns_only_items_changed_strictly_after_it()
    {
        using var client = factory.CreateKeyedClient();
        var (all, _) = await client.GetAllPagesAsync();
        var sample = await client.GetItemAsync("HSC-10021");
        var since = sample.GetProperty("updated_at").GetString()!;

        var (changed, _) = await client.GetAllPagesAsync("limit=5&updated_since=" + Uri.EscapeDataString(since));

        var expected = all.Where(i => string.CompareOrdinal(i.GetProperty("updated_at").GetString(), since) > 0)
            .Select(i => i.GetProperty("item_code").GetString());
        changed.Select(i => i.GetProperty("item_code").GetString()).Should().BeEquivalentTo(expected);
        changed.Should().NotContain(i => i.GetProperty("item_code").GetString() == "HSC-10021");
    }

    [Fact]
    public async Task Updated_since_accepts_an_explicit_utc_offset()
    {
        using var client = factory.CreateKeyedClient();

        var withZ = await (await client.GetAsync("/v2/catalog/items?updated_since=2026-10-02T09:15:00Z", Ct)).ReadJsonAsync();
        var withOffset = await (await client.GetAsync("/v2/catalog/items?updated_since=" + Uri.EscapeDataString("2026-10-02T11:15:00+02:00"), Ct)).ReadJsonAsync();

        withOffset.GetProperty("data").GetRawText().Should().Be(withZ.GetProperty("data").GetRawText());
    }
}
