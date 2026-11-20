using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;

namespace Lumen.IntegrationTests.Checkout;

/// <summary>
/// The partners' live-check endpoints at the HTTP boundary, written from their contracts: Harbor's single item
/// (<c>GET /v2/catalog/items/{code}</c>, JSON) and Kestrel's stock (<c>GET /stock?refs=</c>, semicolon CSV). Each can be
/// switched to answer 503 (down) or to never answer (hang, until the caller gives up).
/// </summary>
public abstract class StubPartner : HttpMessageHandler
{
    private readonly ConcurrentQueue<Uri> _calls = new();
    private TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<Uri> Calls => [.. _calls];

    public bool Down { get; set; }

    public bool Hang { get; set; }

    /// <summary>Completes when the next request arrives (reset with <see cref="ExpectCall"/>).</summary>
    public Task Called => _called.Task;

    public void ExpectCall() => _called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    protected abstract HttpResponseMessage Answer(HttpRequestMessage request);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _calls.Enqueue(request.RequestUri!);
        _called.TrySetResult();
        if (Hang)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        var response = Down ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) } : Answer(request);
        response.RequestMessage = request;
        return response;
    }

    // The client factory recycles primary handlers; a stub must survive that.
    protected override void Dispose(bool disposing)
    {
    }
}

public sealed class StubHarbor : StubPartner
{
    public const string Key = "checkout-harbor-key";

    private readonly ConcurrentDictionary<string, JsonObject> _items = new(StringComparer.Ordinal);

    /// <summary>Puts (or replaces) an item: price in cents (null = no price), LIMITED with <paramref name="onHand"/>, or AVAILABLE when null.</summary>
    public void Set(string code, int? cents, int? onHand = null, bool discontinued = false, string dept = "BAGS")
    {
        var inventory = onHand is { } units
            ? new JsonObject { ["status"] = units == 0 ? "SOLD_OUT" : "LIMITED", ["on_hand"] = units }
            : new JsonObject { ["status"] = "AVAILABLE" };
        _items[code] = new JsonObject
        {
            ["item_code"] = code,
            ["title"] = $"Harbor {code}",
            ["summary"] = "A Harbor item.",
            ["dept"] = dept,
            ["unit_price_cents"] = cents,
            ["currency_code"] = "USD",
            ["media"] = new JsonArray(),
            ["inventory"] = inventory,
            ["props"] = new JsonArray(),
            ["discontinued"] = discontinued,
            ["updated_at"] = "2026-11-20T07:00:00Z",
        };
    }

    public void Remove(string code) => _items.TryRemove(code, out _);

    protected override HttpResponseMessage Answer(HttpRequestMessage request)
    {
        if (!request.Headers.TryGetValues("X-Harbor-Key", out var keys) || keys.Single() != Key)
        {
            return Error(HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        }

        const string prefix = "/v2/catalog/items/";
        var path = request.RequestUri!.AbsolutePath;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return Error(HttpStatusCode.NotFound, "NOT_FOUND");
        }

        return _items.TryGetValue(Uri.UnescapeDataString(path[prefix.Length..]), out var item)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(item.ToJsonString(), Encoding.UTF8, "application/json") }
            : Error(HttpStatusCode.NotFound, "ITEM_NOT_FOUND");
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code) =>
        new(status) { Content = new StringContent("{\"error\":{\"code\":\"" + code + "\",\"message\":\"stub\"}}", Encoding.UTF8, "application/json") };
}

public sealed class StubKestrel : StubPartner
{
    public const string User = "checkout-user";
    public const string Password = "checkout-secret";

    public ConcurrentDictionary<string, int> Stock { get; } = new(StringComparer.Ordinal);

    protected override HttpResponseMessage Answer(HttpRequestMessage request)
    {
        if (request.Headers.Authorization?.Scheme != "Basic")
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new ByteArrayContent([]) };
        }

        if (request.RequestUri!.AbsolutePath != "/stock")
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
        }

        var refs = QueryHelpers.ParseQuery(request.RequestUri.Query).TryGetValue("refs", out var value)
            ? value.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)
            : [];
        var csv = new StringBuilder("ref;units;restock_date\r\n");
        foreach (var reference in refs.Where(Stock.ContainsKey))
        {
            csv.Append(reference).Append(';').Append(Stock[reference]).Append(";\r\n");
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(csv.ToString(), Encoding.UTF8, "text/csv") };
    }
}
