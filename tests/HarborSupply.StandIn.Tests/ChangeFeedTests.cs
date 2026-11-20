using System.Net;
using System.Text.Json;
using static HarborSupply.StandIn.Tests.HttpTestExtensions;

namespace HarborSupply.StandIn.Tests;

/// <summary>
/// The catalogue is Lumen's only change feed (updated_since plus cursor paging), so a change must never be lost:
/// the worst a client may see is the same item twice. Each test changes data, so each gets its own factory.
/// </summary>
public sealed class ChangeFeedTests
{
    private static string Code(JsonElement item) => item.GetProperty("item_code").GetString()!;

    private static string UpdatedAt(JsonElement item) => item.GetProperty("updated_at").GetString()!;

    private static string NewRef() => "LG-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task An_item_changed_between_pages_comes_back_later_in_the_pass_and_nothing_is_skipped()
    {
        await using var factory = new HarborFactory();
        using var client = factory.CreateKeyedClient();
        var (everything, _) = await client.GetAllPagesAsync();

        var firstPage = await (await client.GetAsync("/v2/catalog/items?limit=5", Ct)).ReadJsonAsync();
        var seen = firstPage.GetProperty("data").EnumerateArray().Select(Code).ToList();
        seen.Should().Contain("HSC-20013").And.NotContain("HSC-50053");

        // Mid-pass, one item already returned and one not yet returned both change (an order bumps updated_at).
        factory.Clock.Advance(TimeSpan.FromMilliseconds(250));
        using (var order = await client.PostOrderAsync(NewRef(), ("HSC-20013", 1), ("HSC-50053", 1)))
        {
            order.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        var rest = new List<JsonElement>();
        var cursor = firstPage.GetProperty("next_cursor").GetString();
        while (cursor is not null)
        {
            var page = await (await client.GetAsync("/v2/catalog/items?limit=5&cursor=" + Uri.EscapeDataString(cursor), Ct)).ReadJsonAsync();
            rest.AddRange(page.GetProperty("data").EnumerateArray().Select(e => e.Clone()));
            cursor = page.GetProperty("next_cursor").GetString();
        }

        seen.Concat(rest.Select(Code)).Should().BeEquivalentTo(
            everything.Select(Code).Append("HSC-20013"),
            "every item is returned, and the one changed after it was returned comes back once more");
        rest.TakeLast(2).Select(Code).Should().Equal("HSC-20013", "HSC-50053");
        rest.TakeLast(2).Select(UpdatedAt).Should().AllBe("2026-11-20T12:00:00Z");
    }

    [Fact]
    public async Task A_change_in_the_same_second_as_the_client_watermark_is_not_missed()
    {
        await using var factory = new HarborFactory();
        using var client = factory.CreateKeyedClient();

        factory.Clock.Advance(TimeSpan.FromMilliseconds(200));
        (await client.PostOrderAsync(NewRef(), ("HSC-50053", 1))).StatusCode.Should().Be(HttpStatusCode.Created);
        var (synced, _) = await client.GetAllPagesAsync();
        var watermark = synced.Select(UpdatedAt).Max(StringComparer.Ordinal)!;

        // Later in the same wall-clock second, after the client has read everything.
        factory.Clock.Advance(TimeSpan.FromMilliseconds(400));
        (await client.PostOrderAsync(NewRef(), ("HSC-40047", 1))).StatusCode.Should().Be(HttpStatusCode.Created);
        var (changed, _) = await client.GetAllPagesAsync("updated_since=" + Uri.EscapeDataString(watermark));

        watermark.Should().Be("2026-11-20T12:00:00Z", "JSON shows whole seconds, cut rather than rounded");
        changed.Select(Code).Should().BeEquivalentTo(
            ["HSC-50053", "HSC-40047"],
            "the item behind the watermark may come again, but the later change must not be missed");
    }
}
