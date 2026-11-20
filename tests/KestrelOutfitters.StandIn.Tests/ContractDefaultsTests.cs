using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KestrelOutfitters.StandIn.Tests;

/// <summary>
/// The quirk options exist only so tests can tame them. Out of the box they must be the contract's values:
/// Basic lumen/kestrel-secret, a 2 to 4 s feed, about 1 in 10 feed requests failing, orders RECEIVED for 10 to 30 s.
/// </summary>
public sealed class ContractDefaultsTests(ShippedConfigurationFactory factory) : IClassFixture<ShippedConfigurationFactory>
{
    [Fact]
    public void Option_defaults_are_the_published_contract_values() =>
        AssertContractValues(new KestrelOutfittersOptions());

    [Fact]
    public void Shipped_configuration_keeps_the_contract_values()
    {
        // The test host runs as Development, so this covers appsettings.json and appsettings.Development.json.
        var options = factory.Services.GetRequiredService<IOptions<KestrelOutfittersOptions>>().Value;

        AssertContractValues(options);
    }

    private static void AssertContractValues(KestrelOutfittersOptions options)
    {
        options.Username.Should().Be("lumen");
        options.Password.Should().Be("kestrel-secret");
        options.FeedLatencyMin.Should().Be(TimeSpan.FromSeconds(2));
        options.FeedLatencyMax.Should().Be(TimeSpan.FromSeconds(4));
        options.FeedFailureRate.Should().Be(0.1);
        options.OrderDecisionDelayMin.Should().Be(TimeSpan.FromSeconds(10));
        options.OrderDecisionDelayMax.Should().Be(TimeSpan.FromSeconds(30));
        options.RandomSeed.Should().BeNull("a fixed seed would replay the same 503s and delays on every run");
    }
}
