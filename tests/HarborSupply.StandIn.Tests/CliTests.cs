using System.Text.Json;
using HarborSupply.StandIn.Cli;

namespace HarborSupply.StandIn.Tests;

/// <summary>Demo data changes go through CLI subcommands of the same binary, never through HTTP.</summary>
public sealed class CliTests(HarborFactory factory) : IClassFixture<HarborFactory>
{
    private async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await AdminCli.RunAsync([.. args, "--Harbor:DatabasePath=" + factory.DatabasePath], output, error);
        return (exitCode, output + error.ToString());
    }

    [Fact]
    public async Task Set_commands_change_the_item_and_bump_updated_at()
    {
        using var client = factory.CreateKeyedClient();
        var before = await client.GetItemAsync("HSC-60061");

        (await RunAsync("set-stock", "HSC-60061", "2")).ExitCode.Should().Be(0);
        (await RunAsync("set-price", "HSC-60061", "null")).ExitCode.Should().Be(0);
        (await RunAsync("set-discontinued", "HSC-60061", "true")).ExitCode.Should().Be(0);
        var after = await client.GetItemAsync("HSC-60061");

        after.GetProperty("inventory").GetRawText().Should().Be("{\"status\":\"LIMITED\",\"on_hand\":2}");
        after.GetProperty("unit_price_cents").ValueKind.Should().Be(JsonValueKind.Null);
        after.GetProperty("discontinued").GetBoolean().Should().BeTrue();
        string.CompareOrdinal(after.GetProperty("updated_at").GetString(), before.GetProperty("updated_at").GetString()).Should().BePositive();
    }

    [Fact]
    public async Task List_prints_every_item()
    {
        var (exitCode, output) = await RunAsync("list");

        exitCode.Should().Be(0);
        output.Should().Contain("HSC-10021").And.Contain("Waxed Canvas Tote");
    }

    [Theory]
    [InlineData("set-stock", "HSC-99999", "1")]
    [InlineData("set-stock", "HSC-10022", "-1")]
    [InlineData("set-price", "HSC-10022", "12.50")]
    [InlineData("set-discontinued", "HSC-10022", "maybe")]
    [InlineData("set-stock", "HSC-10022")]
    public async Task Invalid_commands_fail_with_a_non_zero_exit_code(params string[] args)
    {
        var (exitCode, output) = await RunAsync(args);

        exitCode.Should().NotBe(0);
        output.Should().NotBeEmpty();
    }
}
