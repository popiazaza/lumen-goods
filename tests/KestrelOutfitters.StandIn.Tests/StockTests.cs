using System.Net;

namespace KestrelOutfitters.StandIn.Tests;

/// <summary>Contract: GET /stock?refs=... returns semicolon CSV; at most 20 refs; unknown refs left out.</summary>
public sealed class StockTests(KestrelStandInFactory factory) : IClassFixture<KestrelStandInFactory>
{
    [Fact]
    public async Task Stock_is_semicolon_csv_with_the_header_first_and_matches_the_published_example()
    {
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/stock?refs=K-7731,K-7732", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be("text/csv");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Be("ref;units;restock_date\r\nK-7731;3;\r\nK-7732;0;2026-12-01\r\n");
    }

    [Fact]
    public async Task Unknown_refs_are_left_out_without_an_error()
    {
        using var client = factory.CreateAuthenticatedClient();

        var csv = await client.GetStringAsync("/stock?refs=K-0000,K-7732,NOPE", TestContext.Current.CancellationToken);

        csv.Should().Be("ref;units;restock_date\r\nK-7732;0;2026-12-01\r\n");
    }

    [Fact]
    public async Task Only_unknown_refs_return_just_the_header_row()
    {
        using var client = factory.CreateAuthenticatedClient();

        var csv = await client.GetStringAsync("/stock?refs=K-0000", TestContext.Current.CancellationToken);

        csv.Should().Be("ref;units;restock_date\r\n");
    }

    [Fact]
    public async Task Inactive_products_are_still_returned()
    {
        using var client = factory.CreateAuthenticatedClient();

        var csv = await client.GetStringAsync("/stock?refs=K-7734", TestContext.Current.CancellationToken);

        csv.Should().StartWith("ref;units;restock_date\r\nK-7734;");
    }

    [Fact]
    public async Task More_than_20_refs_gets_400_with_a_plain_text_message()
    {
        using var client = factory.CreateAuthenticatedClient();
        var refs = string.Join(',', Enumerable.Range(7700, 21).Select(n => $"K-{n}"));

        using var response = await client.GetAsync($"/stock?refs={refs}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        var message = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        message.Should().Contain("20").And.NotStartWith("<").And.NotStartWith("{");
    }

    [Fact]
    public async Task Exactly_20_entries_are_allowed_and_spaces_and_repeats_among_them_are_answered_once()
    {
        using var client = factory.CreateAuthenticatedClient();
        var twentyEntries = Enumerable.Range(7700, 17).Select(n => $"K-{n}").Concat([" K-7731 ", "K-7731", "K-7700"]);

        using var response = await client.GetAsync(
            $"/stock?refs={Uri.EscapeDataString(string.Join(',', twentyEntries))}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Be("ref;units;restock_date\r\nK-7731;3;\r\n");
    }

    public static TheoryData<string, string> TwentyOneEntries => new()
    {
        { "one ref repeated 21 times", string.Join(',', Enumerable.Repeat("K-7731", 21)) },
        { "20 distinct refs and a repeat", string.Join(',', Enumerable.Range(7700, 20).Select(n => $"K-{n}").Append("K-7700")) },
        { "20 distinct refs and a trailing comma", string.Join(',', Enumerable.Range(7700, 20).Select(n => $"K-{n}")) + "," },
    };

    [Theory]
    [MemberData(nameof(TwentyOneEntries))]
    public async Task More_than_20_entries_get_400_even_when_they_name_fewer_distinct_refs(string because, string refs)
    {
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync($"/stock?refs={refs}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("21");
    }

    [Theory]
    [InlineData("/stock")]
    [InlineData("/stock?refs=")]
    [InlineData("/stock?refs=,%20,")]
    public async Task Missing_or_empty_refs_gets_400_with_a_plain_text_message(string url)
    {
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotBeNullOrWhiteSpace();
    }
}
