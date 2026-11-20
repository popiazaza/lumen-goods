using System.Net;
using System.Text;
using Lumen.Api.Partners;
using Lumen.Api.Partners.Harbor;
using Lumen.Api.Partners.Kestrel;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Lumen.UnitTests.Partners;

/// <summary>A request as the partner received it (headers copied: the message is disposed after the call).</summary>
internal sealed record SeenRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers)
{
    public Dictionary<string, StringValues> Query => QueryHelpers.ParseQuery(Uri.Query);

    public string? Header(string name) => Headers.GetValueOrDefault(name);
}

/// <summary>
/// Plays a partner at the HTTP boundary: the adapters run with their real handlers and resilience pipeline, only the
/// network is replaced. Answers come from a function, so fixtures can be as hostile as the contract allows.
/// </summary>
internal sealed class StubPartner(Func<SeenRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<SeenRequest> _requests = [];

    public IReadOnlyList<SeenRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var seen = new SeenRequest(request.Method, request.RequestUri!, headers);
        lock (_requests)
        {
            _requests.Add(seen);
        }

        var response = respond(seen);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }

    // The client factory disposes primary handlers when it recycles them; a stub must survive that.
    protected override void Dispose(bool disposing)
    {
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Text(string text, string mediaType, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, mediaType) };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };
}

internal static class PartnerTestKit
{
    public const string HarborBase = "http://harbor.test/";
    public const string KestrelBase = "http://kestrel.test/";
    public const string HarborKey = "test-harbor-key";

    /// <summary>The adapters as the API wires them, with stub partners and fast retries.</summary>
    public static ServiceProvider Services(StubPartner? harbor = null, StubPartner? kestrel = null, Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Partners:Harbor:BaseUrl"] = HarborBase,
            ["Partners:Harbor:ApiKey"] = HarborKey,
            ["Partners:Harbor:Resilience:RetryBaseDelay"] = "00:00:00.010",
            ["Partners:Kestrel:BaseUrl"] = KestrelBase,
            ["Partners:Kestrel:Username"] = "lumen",
            ["Partners:Kestrel:Password"] = "test-secret",
            ["Partners:Kestrel:Resilience:RetryBaseDelay"] = "00:00:00.010",
            ["Fx:EurToUsd"] = "1.0850",
        };

        // The spec's mapping tables (the shipped appsettings.json carries the same).
        foreach (var (dept, category) in new[] { ("BAGS", "bags"), ("TEES", "apparel"), ("OUTERWEAR", "apparel"), ("KITCHEN", "home"), ("CAMP", "outdoor"), ("MISC", "accessories") })
        {
            settings[$"Partners:Harbor:CategoryMap:{dept}"] = category;
        }

        foreach (var (group, category) in new[] { ("SOCKS", "apparel"), ("JACKETS", "apparel"), ("BOOTS", "footwear"), ("PACKS", "bags"), ("TENTS", "outdoor"), ("LAMPS", "home"), ("CAPS", "accessories") })
        {
            settings[$"Partners:Kestrel:CategoryMap:{group}"] = category;
        }

        foreach (var (key, value) in overrides ?? [])
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPartners(configuration);
        if (harbor is not null)
        {
            services.AddHttpClient(HarborCatalogSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => harbor);
            services.AddHttpClient(HarborCatalogSource.ImageHttpClientName).ConfigurePrimaryHttpMessageHandler(() => harbor);
        }

        if (kestrel is not null)
        {
            services.AddHttpClient(KestrelCatalogSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => kestrel);
            services.AddHttpClient(KestrelCatalogSource.ImageHttpClientName).ConfigurePrimaryHttpMessageHandler(() => kestrel);
        }

        return services.BuildServiceProvider();
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;
}
