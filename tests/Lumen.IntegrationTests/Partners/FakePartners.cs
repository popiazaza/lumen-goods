using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;

namespace Lumen.IntegrationTests.Partners;

/// <summary>A request as the fake partner saw it.</summary>
public sealed record PartnerCall(string Method, Uri Uri, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.GetValueOrDefault(name);

    public string? Query(string name) => QueryHelpers.ParseQuery(Uri.Query).TryGetValue(name, out var value) ? value.ToString() : null;
}

/// <summary>Base for the fake partners: records calls, can be switched to "down", survives handler recycling.</summary>
public abstract class FakePartner : HttpMessageHandler
{
    /// <summary>A valid 1x1 PNG.</summary>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAwS2OUAAAAABJRU5ErkJggg==");

    private readonly ConcurrentQueue<PartnerCall> _calls = new();

    public IReadOnlyList<PartnerCall> Calls => [.. _calls];

    /// <summary>When true every call answers 503 (the partner is down).</summary>
    public bool Down { get; set; }

    public void ClearCalls() => _calls.Clear();

    protected abstract HttpResponseMessage Answer(PartnerCall call);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var call = new PartnerCall(request.Method.Method, request.RequestUri!, headers);
        _calls.Enqueue(call);
        var response = Down ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) } : Answer(call);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }

    protected override void Dispose(bool disposing)
    {
    }

    protected static HttpResponseMessage Image() =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) { Headers = { { "Content-Type", "image/png" } } } };

    protected static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };
}

/// <summary>
/// A Harbor implementation written from the contract, deliberately unlike our stand-in where the contract is silent:
/// a different cursor format, a page size smaller than asked for, newest-first ordering.
/// </summary>
public sealed class FakeHarbor : FakePartner
{
    public const string Key = "it-harbor-key";

    private readonly List<JsonObject> _items = [];

    public int PageSize { get; set; } = 2;

    public void Set(params JsonObject[] items)
    {
        lock (_items)
        {
            _items.Clear();
            _items.AddRange(items);
        }
    }

    public static JsonObject Item(string code, string dept, int? cents, string status, int? onHand = null, string updatedAt = "2026-11-01T08:00:00Z",
        bool discontinued = false, string? colour = null)
    {
        var inventory = new JsonObject { ["status"] = status };
        if (onHand is not null)
        {
            inventory["on_hand"] = onHand;
        }

        var props = new JsonArray();
        if (colour is not null)
        {
            props.Add(new JsonObject { ["name"] = "colour", ["value"] = colour });
        }

        return new JsonObject
        {
            ["item_code"] = code,
            ["title"] = $"Harbor {code}",
            ["summary"] = $"Summary of {code}.",
            ["dept"] = dept,
            ["unit_price_cents"] = cents,
            ["currency_code"] = "USD",
            ["media"] = new JsonArray(
                new JsonObject { ["kind"] = "image", ["href"] = $"/media/{code}/front.png", ["primary"] = true },
                new JsonObject { ["kind"] = "image", ["href"] = $"/media/{code}/side.png", ["primary"] = false }),
            ["inventory"] = inventory,
            ["props"] = props,
            ["discontinued"] = discontinued,
            ["updated_at"] = updatedAt,
        };
    }

    protected override HttpResponseMessage Answer(PartnerCall call)
    {
        if (call.Header("X-Harbor-Key") != Key)
        {
            return Error(HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        }

        var path = call.Uri.AbsolutePath;
        if (path.StartsWith("/media/", StringComparison.Ordinal))
        {
            return Image();
        }

        if (path != "/v2/catalog/items")
        {
            return Error(HttpStatusCode.NotFound, "NOT_FOUND");
        }

        List<JsonObject> matching;
        lock (_items)
        {
            var since = call.Query("updated_since") is { } s ? DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : (DateTimeOffset?)null;
            matching = [.. _items
                .Where(i => since is null || DateTimeOffset.Parse((string)i["updated_at"]!, System.Globalization.CultureInfo.InvariantCulture) > since)
                .OrderByDescending(i => (string)i["item_code"]!, StringComparer.Ordinal)];
        }

        // Cursor: "~" + offset reversed, so nothing about it looks like our stand-in's base64 keyset cursor.
        var offset = call.Query("cursor") is { } cursor ? int.Parse(new string([.. cursor.TrimStart('~').Reverse()]), System.Globalization.CultureInfo.InvariantCulture) : 0;
        var page = matching.Skip(offset).Take(PageSize).Select(i => (JsonNode?)i.DeepClone()).ToArray();
        var next = offset + PageSize < matching.Count ? "~" + new string([.. (offset + PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture).Reverse()]) : null;
        var body = new JsonObject { ["data"] = new JsonArray(page), ["next_cursor"] = next };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code) =>
        new(status) { Content = new StringContent("{\"error\":{\"code\":\"" + code + "\",\"message\":\"fake\"}}", Encoding.UTF8, "application/json") };
}

/// <summary>
/// A Kestrel implementation written from the contract: feed with Last-Modified/304, CSV stock, pictures. Pictures on
/// <see cref="CdnHost"/> play a partner CDN: public, no credentials expected.
/// </summary>
public sealed class FakeKestrel : FakePartner
{
    public const string User = "it-user";
    public const string Password = "it-secret";
    public const string CdnHost = "cdn.kestrel.test";

    public string FeedXml { get; set; } = "";

    public string LastModified { get; set; } = "Wed, 18 Nov 2026 09:00:00 GMT";

    public ConcurrentDictionary<string, int> Stock { get; } = new();

    public static string Product(string reference, string group, string price, string active = "Y", string? nameEn = null, string? nameDe = null,
        string? descEn = null, bool large = true, bool thumb = true) =>
        $"""
          <Product ref="{reference}">
            {(nameDe is null ? "" : $"<Name lang=\"de\">{nameDe}</Name>")}
            {(nameEn is null ? "" : $"<Name lang=\"en\">{nameEn}</Name>")}
            {(descEn is null ? "" : $"<Desc lang=\"en\"><![CDATA[{descEn}]]></Desc>")}
            <Group>{group}</Group>
            <Price currency="EUR">{price}</Price>
            <Pictures>{(thumb ? $"<Pic size=\"thumb\">http://kestrel.test/img/{reference}_t.png</Pic>" : "")}{(large ? $"<Pic size=\"large\">http://kestrel.test/img/{reference}_l.png</Pic>" : "")}</Pictures>
            <Specs><Spec key="Colour">Red</Spec></Specs>
            <Active>{active}</Active>
          </Product>
        """;

    public void SetFeed(params string[] products) =>
        FeedXml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Catalogue generated="2026-11-18T10:00:00+01:00">
            {string.Join("\n", products)}
            </Catalogue>
            """;

    protected override HttpResponseMessage Answer(PartnerCall call)
    {
        if (call.Uri.Host == CdnHost)
        {
            return call.Uri.AbsolutePath.StartsWith("/img/", StringComparison.Ordinal) ? Image() : Status(HttpStatusCode.NotFound);
        }

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));
        if (call.Header("Authorization") != expected)
        {
            return Status(HttpStatusCode.Unauthorized);
        }

        switch (call.Uri.AbsolutePath)
        {
            case "/feed/products.xml":
                if (call.Header("If-Modified-Since") == LastModified)
                {
                    return Status(HttpStatusCode.NotModified);
                }

                var feed = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FeedXml, Encoding.UTF8, "application/xml") };
                feed.Content.Headers.TryAddWithoutValidation("Last-Modified", LastModified);
                return feed;
            case "/stock":
                var refs = (call.Query("refs") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (refs.Length > 20)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("Too many refs", Encoding.UTF8, "text/plain") };
                }

                var csv = new StringBuilder("ref;units;restock_date\r\n");
                foreach (var reference in refs.Where(Stock.ContainsKey))
                {
                    csv.Append(reference).Append(';').Append(Stock[reference]).Append(";\r\n");
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(csv.ToString(), Encoding.UTF8, "text/csv") };
            default:
                return call.Uri.AbsolutePath.StartsWith("/img/", StringComparison.Ordinal) ? Image() : Status(HttpStatusCode.NotFound);
        }
    }
}
