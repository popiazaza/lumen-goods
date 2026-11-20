using System.Net;
using static HarborSupply.StandIn.Tests.HttpTestExtensions;

namespace HarborSupply.StandIn.Tests;

/// <summary>Contract: 60 requests per minute per key; over the limit, 429 with Retry-After in seconds.</summary>
public sealed class RateLimitTests
{
    [Fact]
    [Trait("CT", "CT-H-02")]
    public async Task CT_H_02_61st_request_in_a_window_returns_429_with_Retry_After_until_the_window_resets()
    {
        await using var factory = new HarborFactory { RequestsPerMinute = null }; // contract default: 60
        using var client = factory.CreateKeyedClient();

        for (var i = 0; i < 59; i++)
        {
            using var ok = await client.GetAsync("/v2/catalog/items?limit=1", Ct);
            ok.StatusCode.Should().Be(HttpStatusCode.OK, $"request {i + 1} is within the limit");
        }

        // Images count too: "every request" needs the key, so every request is counted.
        using var sixtieth = await client.GetAsync("/media/HSC-10021/front.png", Ct);
        sixtieth.StatusCode.Should().Be(HttpStatusCode.OK);

        factory.Clock.Advance(TimeSpan.FromSeconds(20.5));
        using var limited = await client.GetAsync("/v2/catalog/items/HSC-10021", Ct);
        await limited.ShouldBeHarborErrorAsync(429, "RATE_LIMITED");
        limited.Headers.GetValues("Retry-After").Should().Equal("40");

        factory.Clock.Advance(TimeSpan.FromSeconds(39));
        using var stillLimited = await client.GetAsync("/v2/catalog/items/HSC-10021", Ct);
        await stillLimited.ShouldBeHarborErrorAsync(429, "RATE_LIMITED");
        stillLimited.Headers.GetValues("Retry-After").Should().ContainSingle().Which.Should().Be("1", "half a second left rounds up to 1");

        factory.Clock.Advance(TimeSpan.FromSeconds(0.5));
        using var nextWindow = await client.GetAsync("/v2/catalog/items/HSC-10021", Ct);
        nextWindow.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Requests_rejected_with_401_do_not_count_toward_the_limit()
    {
        await using var factory = new HarborFactory { RequestsPerMinute = null };
        using var anonymous = factory.CreateClient();
        using var client = factory.CreateKeyedClient();

        for (var i = 0; i < 30; i++)
        {
            using var unauthorised = await anonymous.GetAsync("/v2/catalog/items", Ct);
            unauthorised.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        for (var i = 0; i < 60; i++)
        {
            using var ok = await client.GetAsync("/v2/catalog/items?limit=1", Ct);
            ok.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var limited = await client.GetAsync("/v2/catalog/items?limit=1", Ct);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.GetValues("Retry-After").Should().Equal("60");
    }
}
