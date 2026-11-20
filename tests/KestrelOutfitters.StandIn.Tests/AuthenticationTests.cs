using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace KestrelOutfitters.StandIn.Tests;

/// <summary>Contract: every request uses Basic lumen/kestrel-secret; failure is 401 with an empty body.</summary>
public sealed class AuthenticationTests(KestrelStandInFactory factory) : IClassFixture<KestrelStandInFactory>
{
    public static TheoryData<string, string> EveryRoute => new()
    {
        { "GET", "/feed/products.xml" },
        { "GET", "/stock?refs=K-7731" },
        { "POST", "/orders" },
        { "GET", "/orders/KT-5521" },
        { "GET", "/img/k7731_l.png" },
        { "GET", "/does-not-exist" },
    };

    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task Request_without_credentials_gets_401_with_an_empty_body(string method, string path)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = new StringContent("<PurchaseOrder/>", Encoding.UTF8, "application/xml");
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        response.Headers.WwwAuthenticate.Should().ContainSingle().Which.Scheme.Should().Be("Basic");
    }

    [Theory]
    [InlineData("Basic", "lumen:wrong-secret")]
    [InlineData("Basic", "someone:kestrel-secret")]
    [InlineData("Basic", "lumen:KESTREL-SECRET")]
    [InlineData("Basic", "lumenkestrel-secret")]
    [InlineData("Bearer", "lumen:kestrel-secret")]
    public async Task Wrong_credentials_get_401_with_an_empty_body(string scheme, string credentials)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(scheme, Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));

        using var response = await client.GetAsync("/stock?refs=K-7731", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Malformed_basic_header_gets_401_with_an_empty_body()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Basic !!!not-base64!!!");

        using var response = await client.GetAsync("/stock?refs=K-7731", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Correct_credentials_are_accepted()
    {
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/stock?refs=K-7731", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Authenticated_request_with_the_wrong_method_gets_405_with_an_empty_body()
    {
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.DeleteAsync("/feed/products.xml", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Authenticated_request_to_an_unknown_route_gets_404_with_an_empty_body()
    {
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/does-not-exist", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
    }
}
