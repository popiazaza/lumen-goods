using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Lumen.IntegrationTests.Catalog;

namespace Lumen.IntegrationTests.Infrastructure;

/// <summary>What every request goes through: health endpoints, caching headers and the request body limit.</summary>
public sealed class PipelineTests(LumenApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Liveness_answers_healthy_without_running_the_database_check()
    {
        var response = await factory.CreateClient().GetAsync("/health/live", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Be("Healthy");
    }

    [Fact]
    public async Task A_request_body_over_1_mb_is_refused_with_413_as_a_problem_even_without_nginx_in_front()
    {
        // The in-process test server has no body limit of its own; Kestrel does, so this runs the API on Kestrel, as in
        // production behind Front Door.
        await using var app = factory.WithWebHostBuilder(_ => { });
        app.UseKestrel(0);
        app.StartServer();
        using var client = app.CreateClient();

        using var atLimit = await client.PostAsync("/api/v1/cart/lines", JsonBodyOf(1_048_576), Ct);
        using var overLimit = await client.PostAsync("/api/v1/cart/lines", JsonBodyOf(1_048_577), Ct);

        atLimit.StatusCode.Should().NotBe(HttpStatusCode.RequestEntityTooLarge);
        overLimit.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        overLimit.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await overLimit.ReadJsonAsync()).GetProperty("status").GetInt32().Should().Be(413);
    }

    /// <summary>An add-to-cart body padded with an extra property to exactly <paramref name="bytes"/> bytes.</summary>
    private static ByteArrayContent JsonBodyOf(int bytes)
    {
        var start = $$"""{"productId":"{{Guid.NewGuid()}}","quantity":1,"padding":""";
        var body = Encoding.UTF8.GetBytes(start + new string('x', bytes - start.Length - 2) + "\"}");
        body.Length.Should().Be(bytes);
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    // /media keeps its own caching: PartnerEndpointTests checks its max-age.
    [Theory]
    [InlineData("/api/v1/auth/session")]
    [InlineData("/api/v1/products")]
    [InlineData("/api/v1/orders")] // 401: answered by authorization, before any endpoint runs
    public async Task Every_api_answer_is_marked_no_store(string url)
    {
        var response = await factory.CreateClient().GetAsync(url, Ct);

        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }
}
