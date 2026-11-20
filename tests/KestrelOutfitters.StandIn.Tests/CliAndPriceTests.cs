using System.Net;
using System.Xml.Linq;
using KestrelOutfitters.StandIn.Catalogue;

namespace KestrelOutfitters.StandIn.Tests;

/// <summary>Contract number format: comma decimal, dot thousands (e.g. 1.249,00).</summary>
public sealed class EuroPriceTests
{
    [Theory]
    [InlineData("14.95", "14,95")]
    [InlineData("1249", "1.249,00")]
    [InlineData("0.5", "0,50")]
    [InlineData("999.99", "999,99")]
    [InlineData("1234567.8", "1.234.567,80")]
    public void Formats_with_comma_decimal_and_dot_thousands(string invariant, string expected) =>
        EuroPrice.Format(decimal.Parse(invariant, System.Globalization.CultureInfo.InvariantCulture)).Should().Be(expected);

    [Theory]
    [InlineData("14,95", "14.95")]
    [InlineData("1.249,00", "1249.00")]
    [InlineData("1249,00", "1249.00")]
    [InlineData("0,50", "0.50")]
    public void Parses_the_contract_format(string text, string invariant)
    {
        EuroPrice.TryParse(text, out var price).Should().BeTrue();
        price.Should().Be(decimal.Parse(invariant, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("14.95")]
    [InlineData("1,249.00")]
    [InlineData("12,5")]
    [InlineData("-5,00")]
    [InlineData("0,00")]
    [InlineData("1.24,00")]
    [InlineData("abc")]
    [InlineData("")]
    public void Rejects_anything_else(string text) => EuroPrice.TryParse(text, out _).Should().BeFalse();
}

/// <summary>Demo data changes go through CLI subcommands of the same binary, never HTTP.</summary>
public sealed class CliTests(KestrelStandInFactory factory) : IClassFixture<KestrelStandInFactory>
{
    [Fact]
    public async Task Set_stock_with_a_restock_date_shows_up_in_the_stock_csv()
    {
        var (exitCode, _) = await factory.RunCliAsync("set-stock", "K-7751", "0", "2027-02-01");

        using var client = factory.CreateAuthenticatedClient();
        var csv = await client.GetStringAsync("/stock?refs=K-7751", TestContext.Current.CancellationToken);
        exitCode.Should().Be(0);
        csv.Should().Be("ref;units;restock_date\r\nK-7751;0;2027-02-01\r\n");
    }

    [Fact]
    public async Task Set_price_and_set_active_show_up_in_the_feed()
    {
        (await factory.RunCliAsync("set-price", "K-7753", "1.099,50")).ExitCode.Should().Be(0);
        (await factory.RunCliAsync("set-active", "K-7753", "N")).ExitCode.Should().Be(0);

        using var client = factory.CreateAuthenticatedClient();
        var feed = XDocument.Parse(await client.GetStringAsync("/feed/products.xml", TestContext.Current.CancellationToken));
        var product = feed.Root!.Elements("Product").Single(p => (string)p.Attribute("ref")! == "K-7753");
        product.Element("Price")!.Value.Should().Be("1.099,50");
        product.Element("Active")!.Value.Should().Be("N");
    }

    [Fact]
    public async Task List_prints_every_product()
    {
        var (exitCode, output) = await factory.RunCliAsync("list");

        exitCode.Should().Be(0);
        output.Should().Contain("K-7731").And.Contain("14,95").And.Contain("GLOVES");
    }

    [Theory]
    [InlineData("set-stock", "K-0000", "1")]
    [InlineData("set-stock", "K-7731", "-1")]
    [InlineData("set-stock", "K-7731", "ten")]
    [InlineData("set-stock", "K-7731", "1", "01.12.2026")]
    [InlineData("set-stock", "K-7731")]
    [InlineData("set-price", "K-7731", "14.95")]
    [InlineData("set-active", "K-7731", "yes")]
    public async Task Bad_arguments_fail_with_exit_code_1_and_change_nothing(params string[] args)
    {
        var (exitCode, output) = await factory.RunCliAsync(args);

        exitCode.Should().Be(1);
        output.Should().NotBeNullOrWhiteSpace();
        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.GetAsync("/stock?refs=K-7731", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("K-7731;3;");
    }

    [Fact]
    public void Only_known_subcommands_switch_the_binary_into_cli_mode()
    {
        Cli.IsCommand(["set-stock", "K-7731", "0"]).Should().BeTrue();
        Cli.IsCommand(["list"]).Should().BeTrue();
        Cli.IsCommand([]).Should().BeFalse();
        Cli.IsCommand(["--urls", "http://+:8080"]).Should().BeFalse();
    }
}
