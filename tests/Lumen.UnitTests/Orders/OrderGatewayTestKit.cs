using System.Net;
using System.Text;
using Lumen.Api.Orders;
using Lumen.Api.Partners;
using Lumen.Api.Partners.Harbor;
using Lumen.Api.Partners.Kestrel;
using Lumen.Api.Partners.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Lumen.UnitTests.Orders;

/// <summary>A request as the partner received it, body included (copied: the message is disposed after the call).</summary>
internal sealed record SentRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string? ContentType, string Body)
{
    public string? Header(string name) => Headers.GetValueOrDefault(name);
}

/// <summary>Plays a partner's order endpoints at the HTTP boundary; the gateways run with their real clients and pipeline.</summary>
internal sealed class OrderStub(Func<SentRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<SentRequest> _requests = [];

    public IReadOnlyList<SentRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var sent = new SentRequest(request.Method, request.RequestUri!, headers, request.Content?.Headers.ContentType?.ToString(), body);
        lock (_requests)
        {
            _requests.Add(sent);
        }

        var response = respond(sent);
        response.RequestMessage = request;
        return response;
    }

    // The client factory disposes primary handlers when it recycles them; a stub must survive that.
    protected override void Dispose(bool disposing)
    {
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Xml(HttpStatusCode status, string xml) =>
        new(status) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };

    public static HttpResponseMessage Empty(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };
}

internal static class OrderGatewayTestKit
{
    public const string HarborKey = "unit-harbor-key";
    public const string KestrelUser = "lumen";
    public const string KestrelPassword = "unit-secret";

    public static readonly PartnerShipTo ShipTo = new("Ana Silva", "12 Harbour Road", null, "Portland", "OR", "97201", "US");

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The order gateways as the API wires them (AddPartners + AddOrders' gateways), with stub partners.</summary>
    public static ServiceProvider Services(OrderStub? harbor = null, OrderStub? kestrel = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Partners:Harbor:BaseUrl"] = "http://harbor.test/",
            ["Partners:Harbor:ApiKey"] = HarborKey,
            ["Partners:Harbor:Resilience:RetryBaseDelay"] = "00:00:00.010",
            ["Partners:Kestrel:BaseUrl"] = "http://kestrel.test/",
            ["Partners:Kestrel:Username"] = KestrelUser,
            ["Partners:Kestrel:Password"] = KestrelPassword,
            ["Partners:Kestrel:Resilience:RetryBaseDelay"] = "00:00:00.010",
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPartners(configuration);
        services.AddPartnerOrderGateways();
        if (harbor is not null)
        {
            services.AddHttpClient(HarborCatalogSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => harbor);
        }

        if (kestrel is not null)
        {
            services.AddHttpClient(KestrelCatalogSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => kestrel);
        }

        return services.BuildServiceProvider();
    }

    public static IPartnerOrderGateway Gateway(this ServiceProvider services, string partner) =>
        services.GetServices<IPartnerOrderGateway>().Single(g => g.Partner == partner);

    public static PartnerOrderRequest Request(string externalRef = "LG-7K3M9QX2-2", string item = "HSC-10021", int quantity = 2, PartnerShipTo? shipTo = null) =>
        new(externalRef, item, quantity, shipTo ?? ShipTo);
}
