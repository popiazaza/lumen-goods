using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace HarborSupply.StandIn.Tests;

/// <summary>
/// Runs the stand-in in memory against its own temporary SQLite file and a fake clock, so tests never
/// sleep and never share data. The rate limit is raised by default so ordinary tests cannot trip it;
/// <see cref="RateLimitTests"/> builds factories with the contract default of 60.
/// </summary>
public sealed class HarborFactory : WebApplicationFactory<Program>
{
    public const string ValidKey = "harbor-dev-key-123";

    // After every seeded updated_at, so writes made "now" sort last.
    public static readonly DateTimeOffset Start = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "harbor-tests-" + Guid.NewGuid().ToString("N"));

    public HarborFactory() => Directory.CreateDirectory(_directory);

    /// <summary>Null keeps the configured value (the contract's 60).</summary>
    public int? RequestsPerMinute { get; init; } = 100_000;

    public FakeTimeProvider Clock { get; } = new(Start);

    public string DatabasePath => Path.Combine(_directory, "harbor.db");

    public HttpClient CreateKeyedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Harbor-Key", ValidKey);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Harbor:DatabasePath", DatabasePath);
        if (RequestsPerMinute is { } limit)
        {
            builder.UseSetting("Harbor:RequestsPerMinute", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp file left behind is harmless.
        }
    }
}

internal static class HttpTestExtensions
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>Follows next_cursor to the end and returns every item plus the raw text of each page.</summary>
    public static async Task<(List<JsonElement> Items, List<string> Pages)> GetAllPagesAsync(this HttpClient client, string query = "limit=25")
    {
        var items = new List<JsonElement>();
        var pages = new List<string>();
        string? cursor = null;
        do
        {
            var url = "/v2/catalog/items?" + query + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            using var response = await client.GetAsync(url, Ct);
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
            var text = await response.Content.ReadAsStringAsync(Ct);
            pages.Add(text);
            using var document = JsonDocument.Parse(text);
            items.AddRange(document.RootElement.GetProperty("data").EnumerateArray().Select(e => e.Clone()));
            var next = document.RootElement.GetProperty("next_cursor");
            cursor = next.ValueKind == JsonValueKind.Null ? null : next.GetString();
            pages.Count.Should().BeLessThan(100, "paging must terminate");
        }
        while (cursor is not null);

        return (items, pages);
    }

    public static async Task<JsonElement> GetItemAsync(this HttpClient client, string itemCode)
    {
        using var response = await client.GetAsync("/v2/catalog/items/" + itemCode, Ct);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        return await response.ReadJsonAsync();
    }

    public static Task<HttpResponseMessage> PostOrderAsync(this HttpClient client, string externalRef, params (string ItemCode, int Qty)[] lines) =>
        client.PostAsJsonAsync("/v2/orders", new
        {
            external_ref = externalRef,
            lines = lines.Select(l => new { item_code = l.ItemCode, qty = l.Qty }),
            ship_to = new
            {
                name = "Ana Silva",
                line1 = "12 Harbour Road",
                line2 = (string?)null,
                city = "Portland",
                region = "OR",
                postal_code = "97201",
                country_iso2 = "US",
            },
        }, Ct);

    public static Task<HttpResponseMessage> PostRawOrderAsync(this HttpClient client, string json) =>
        client.PostAsync("/v2/orders", new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

    /// <summary>Asserts the Harbor standard error body: JSON (never problem+json) with error.code and error.message.</summary>
    public static async Task<JsonElement> ShouldBeHarborErrorAsync(this HttpResponseMessage response, int status, string code)
    {
        ((int)response.StatusCode).Should().Be(status);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        var body = await response.ReadJsonAsync();
        var error = body.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(code);
        error.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        body.EnumerateObject().Select(p => p.Name).Should().Equal("error");
        return error;
    }
}
