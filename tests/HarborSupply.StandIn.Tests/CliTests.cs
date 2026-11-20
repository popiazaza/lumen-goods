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

    public static TheoryData<string[], bool> Invocations => new()
    {
        { [], false },
        { ["--Harbor:DatabasePath=/tmp/harbor.db"], false },
        { ["--urls", "http://+:8080"], false },
        { ["Harbor:RequestsPerMinute=5"], false },
        { ["list"], true },
        { ["--Harbor:DatabasePath=/tmp/harbor.db", "set-stock", "HSC-10021", "0"], true },
        { ["bogus"], true },
        { ["--urls", "http://+:8080", "lsit"], true },
    };

    [Theory]
    [MemberData(nameof(Invocations))]
    public void Any_command_word_selects_the_CLI_and_settings_alone_start_the_server(string[] args, bool isCli) =>
        AdminCli.IsCliInvocation(args).Should().Be(isCli);

    [Fact]
    public async Task A_setting_before_the_command_is_applied()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await AdminCli.RunAsync(["--Harbor:DatabasePath=" + factory.DatabasePath, "list"], output, error);

        exitCode.Should().Be(0, error.ToString());
        output.ToString().Should().Contain("HSC-10021");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_command_prints_usage_and_exits_2_without_starting_the_server(bool settingFirst)
    {
        // Through the real entry point: before the fix this started a second web server and crashed on the port.
        var databasePath = Path.Combine(Path.GetTempPath(), "harbor-cli-" + Guid.NewGuid().ToString("N"), "harbor.db");
        var setting = "--Harbor:DatabasePath=" + databasePath;
        string[] args = settingFirst ? [setting, "bogus"] : ["bogus", setting];
        var main = typeof(Program).Assembly.EntryPoint!;

        // The timeout turns a regression (a server that never returns) into a failure instead of a hung run.
        var exitCode = await Task.Run(() => (int)main.Invoke(null, [args])!, HttpTestExtensions.Ct).WaitAsync(TimeSpan.FromSeconds(30), HttpTestExtensions.Ct);

        exitCode.Should().Be(2);
        File.Exists(databasePath).Should().BeFalse("an unknown command touches nothing");
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
