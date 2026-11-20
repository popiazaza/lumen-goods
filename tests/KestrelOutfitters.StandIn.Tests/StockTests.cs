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
    public async Task Exactly_20_refs_are_allowed_and_duplicates_and_spaces_do_not_count()
    {
        using var client = factory.CreateAuthenticatedClient();
        var twenty = Enumerable.Range(7700, 19).Select(n => $"K-{n}").Append("K-7731").ToList();
        var withDuplicates = string.Join(',', twenty.Concat([" K-7731 ", "K-7700", "K-7731"]));

        using var exactly20 = await client.GetAsync($"/stock?refs={string.Join(',', twenty)}", TestContext.Current.CancellationToken);
        using var duplicated = await client.GetAsync($"/stock?refs={Uri.EscapeDataString(withDuplicates)}", TestContext.Current.CancellationToken);

        exactly20.StatusCode.Should().Be(HttpStatusCode.OK);
        duplicated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await duplicated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Split("\r\n").Count(line => line.StartsWith("K-7731;", StringComparison.Ordinal)).Should().Be(1);
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
