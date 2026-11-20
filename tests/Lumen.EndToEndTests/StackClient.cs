using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lumen.EndToEndTests;

/// <summary>
/// A client of a running Lumen Goods stack that behaves like the storefront in a browser: it keeps cookies and echoes the
/// XSRF-TOKEN cookie in the X-XSRF-TOKEN header on state-changing requests. It knows the API only by its HTTP contract
/// (paths and JSON), never by the code, so these tests also hold for a deployed stack.
/// </summary>
public sealed class StackClient : IDisposable
{
    /// <summary>The stack's address, e.g. http://localhost:8080 for docker compose. Unset: every test is skipped.</summary>
    public const string UrlVariable = "LUMEN_E2E_URL";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CookieContainer _cookies = new();
    private readonly Uri _baseUri;
    private readonly HttpClient _http;

    private StackClient(Uri baseUri)
    {
        _baseUri = baseUri;
        _http = new HttpClient(new HttpClientHandler { CookieContainer = _cookies }) { BaseAddress = baseUri };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Opens a session the way the storefront does on load (which issues the XSRF cookie). Skips the calling test (xUnit
    /// dynamic skip) when <see cref="UrlVariable"/> is not set, so the solution's test run stays green without a stack.
    /// </summary>
    public static async Task<StackClient> StartAsync()
    {
        var url = Environment.GetEnvironmentVariable(UrlVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(url),
            $"{UrlVariable} is not set, so there is no running stack to test (e.g. {UrlVariable}=http://localhost:8080 after docker compose up).");

        var client = new StackClient(new Uri(url.TrimEnd('/') + "/"));
        await client.GetAsync("auth/session");
        return client;
    }

    public Task<JsonNode> SignInAsync(string email, string password) => PostAsync("auth/sign-in", new { email, password });

    public Task<JsonNode> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null, HttpStatusCode.OK);

    public Task<JsonNode> PostAsync(string path, object body, HttpStatusCode expected = HttpStatusCode.OK) =>
        SendAsync(HttpMethod.Post, path, body, expected);

    public Task<JsonNode> DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path, null, HttpStatusCode.OK);

    /// <summary>Removes every line from the signed-in customer's cart, so an order holds exactly what the test adds.</summary>
    public async Task EmptyCartAsync()
    {
        var cart = await GetAsync("cart");
        foreach (var line in cart["lines"]!.AsArray())
        {
            await DeleteAsync($"cart/lines/{line!["id"]!.GetValue<string>()}");
        }
    }

    /// <summary>
    /// Reads until <paramref name="read"/> returns a value, for at most <paramref name="timeout"/>. Real time on purpose:
    /// a running stack has no fake clock, and partner sync, payments and partner orders run in its background jobs.
    /// </summary>
    public static async Task<T> EventuallyAsync<T>(Func<Task<T?>> read, TimeSpan timeout, string what)
        where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (await read() is { } value)
            {
                return value;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited {timeout.TotalSeconds:0} s for {what}.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, object? body, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(method, "api/v1/" + path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        if (method != HttpMethod.Get && _cookies.GetCookies(_baseUri)["XSRF-TOKEN"] is { } xsrf)
        {
            request.Headers.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(xsrf.Value));
        }

        using var response = await _http.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(expected, "{0} /api/v1/{1} answered {2}", method, path, text);
        return text.Length == 0 ? new JsonObject() : JsonNode.Parse(text)!;
    }
}
