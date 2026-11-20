using System.Net;

namespace Lumen.IntegrationTests.Infrastructure;

/// <summary>What every request goes through: health endpoints and caching headers.</summary>
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
