using System.Net;
using Lumen.Api.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lumen.UnitTests.Infrastructure;

/// <summary>
/// Which peers' X-Forwarded-For the API believes: the configured proxies (nginx in docker compose) and loopback, never a
/// whole private range, so another container cannot pick its own client address for the per-IP limits.
/// </summary>
public sealed class TrustedProxyOptionsTests
{
    [Theory]
    [InlineData("nginx")]
    [InlineData("172.16.87.0/24")]
    public void A_known_proxy_that_is_not_an_ip_address_is_refused_at_start(string proxy)
    {
        using var services = Services(proxy);

        var act = () => services.GetRequiredService<IStartupValidator>().Validate();

        act.Should().Throw<OptionsValidationException>().WithMessage("*KnownProxies*");
    }

    [Fact]
    public void Only_the_known_proxies_and_loopback_are_believed()
    {
        using var services = Services("172.16.87.10");
        services.GetRequiredService<IStartupValidator>().Validate();

        var options = services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        options.KnownProxies.Should().Contain(IPAddress.Parse("172.16.87.10"));
        options.KnownProxies.Concat(options.KnownIPNetworks.Select(n => n.BaseAddress))
            .Where(a => !a.Equals(IPAddress.Parse("172.16.87.10")))
            .Should().OnlyContain(a => IPAddress.IsLoopback(a));
    }

    private static ServiceProvider Services(params string[] proxies) => new ServiceCollection()
        .AddReverseProxySupport(new ConfigurationBuilder()
            .AddInMemoryCollection(proxies.Select((proxy, i) => new KeyValuePair<string, string?>($"ForwardedHeaders:KnownProxies:{i}", proxy)))
            .Build())
        .BuildServiceProvider();
}
