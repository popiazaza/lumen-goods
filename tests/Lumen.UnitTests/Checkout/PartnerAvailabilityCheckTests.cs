using System.Net;
using Lumen.Api.Partners.Harbor;
using Lumen.Api.Partners.Kestrel;
using Lumen.Api.Partners.Ports;
using Lumen.UnitTests.Partners;
using Microsoft.Extensions.DependencyInjection;

namespace Lumen.UnitTests.Checkout;

/// <summary>
/// BR-CHK-02: the partners' live checks at the HTTP boundary, on the partners' real named clients (credentials,
/// resilience, Harbor's rate limiter); only the network is a stub.
/// </summary>
public sealed class PartnerAvailabilityCheckTests
{
    private static readonly Guid LineId = Guid.NewGuid();

    private static string HarborItem(string code, string cents = "4990", string inventory = "{\"status\":\"AVAILABLE\"}", bool discontinued = false,
        string dept = "BAGS") =>
        $$"""
        {"item_code":"{{code}}","title":"Canvas Tote","summary":"Sturdy.","dept":"{{dept}}","unit_price_cents":{{cents}},
         "currency_code":"USD","media":[],"inventory":{{inventory}},"props":[],"discontinued":{{(discontinued ? "true" : "false")}},
         "updated_at":"2026-11-20T07:00:00Z"}
        """;

    private static async Task<PartnerLineCheckResult> CheckHarborAsync(StubPartner harbor, int quantity = 1, decimal expected = 49.90m)
    {
        using var services = PartnerTestKit.Services(harbor: harbor);
        var check = ActivatorUtilities.CreateInstance<HarborAvailabilityCheck>(services);
        return (await check.CheckAsync([new PartnerLineCheck(LineId, "HSC-1", quantity, expected)], PartnerTestKit.Ct)).Single();
    }

    private static async Task<PartnerLineCheckResult> CheckKestrelAsync(StubPartner kestrel, int quantity)
    {
        using var services = PartnerTestKit.Services(kestrel: kestrel);
        var check = ActivatorUtilities.CreateInstance<KestrelAvailabilityCheck>(services);
        return (await check.CheckAsync([new PartnerLineCheck(LineId, "K-7731", quantity, 16.22m)], PartnerTestKit.Ct)).Single();
    }

    [Fact]
    [Trait("BR", "BR-CHK-02")]
    public async Task BR_CHK_02_harbor_reads_the_single_item_with_its_key_and_confirms_an_available_line()
    {
        var harbor = new StubPartner(_ => StubPartner.Json(HarborItem("HSC-1")));

        var result = await CheckHarborAsync(harbor, quantity: 3);

        result.Should().Be(new PartnerLineCheckResult(LineId, PartnerLineCheckOutcome.Ok, 49.90m, null));
        var request = harbor.Requests.Should().ContainSingle().Subject;
        request.Uri.AbsolutePath.Should().Be("/v2/catalog/items/HSC-1");
        request.Header(HarborKeyHandler.HeaderName).Should().Be(PartnerTestKit.HarborKey);
    }

    [Theory]
    [Trait("BR", "BR-CHK-02")]
    [InlineData(2, new[] { "HSC-LAST", "HSC-TIGHT" })]
    [InlineData(4, new[] { "HSC-LAST", "HSC-TIGHT", "HSC-ROOMY", "HSC-PLENTY" })]
    public async Task BR_CHK_02_harbor_asks_only_max_live_checks_lines_tightest_first_and_returns_the_rest_not_checked(
        int maxLiveChecks, string[] asked)
    {
        var harbor = new StubPartner(request => StubPartner.Json(HarborItem(request.Uri.Segments[^1])));
        using var services = PartnerTestKit.Services(harbor: harbor,
            overrides: new() { ["Partners:Harbor:MaxLiveChecks"] = maxLiveChecks.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var check = ActivatorUtilities.CreateInstance<HarborAvailabilityCheck>(services);
        PartnerLineCheck Line(string code, int quantity, int? known) => new(Guid.NewGuid(), code, quantity, 49.90m, known);
        var lines = new[]
        {
            Line("HSC-PLENTY", quantity: 4, known: null), // no number: after every known one, the largest quantity first
            Line("HSC-ROOMY", quantity: 1, known: 10), // known, 9 to spare
            Line("HSC-ONE", quantity: 1, known: null),
            Line("HSC-LAST", quantity: 3, known: 3), // known, nothing to spare: asked first
            Line("HSC-TIGHT", quantity: 1, known: 2), // known, 1 to spare
        };

        var results = (await check.CheckAsync(lines, PartnerTestKit.Ct)).ToDictionary(r => r.LineId);

        harbor.Requests.Select(r => r.Uri.Segments[^1]).Should().BeEquivalentTo(asked);
        lines.Should().AllSatisfy(l => results[l.LineId].Outcome.Should().Be(
            asked.Contains(l.SourceProductId) ? PartnerLineCheckOutcome.Ok : PartnerLineCheckOutcome.NotChecked));
    }

    [Theory]
    [Trait("BR", "BR-CHK-01")]
    [InlineData("5490", 54.90)]
    [InlineData("4490", 44.90)]
    public async Task BR_CHK_01_harbor_reports_a_live_price_up_or_down(string cents, decimal live)
    {
        var result = await CheckHarborAsync(new StubPartner(_ => StubPartner.Json(HarborItem("HSC-1", cents))));

        (result.Outcome, result.CurrentUnitPriceUsd).Should().Be((PartnerLineCheckOutcome.PriceChanged, live));
    }

    [Fact]
    [Trait("BR", "BR-CHK-02")]
    public async Task BR_CHK_02_harbor_limited_stock_below_the_line_is_short_with_the_number()
    {
        var result = await CheckHarborAsync(new StubPartner(_ => StubPartner.Json(HarborItem("HSC-1", inventory: "{\"status\":\"LIMITED\",\"on_hand\":1}"))), quantity: 2);

        (result.Outcome, result.AvailableQuantity).Should().Be((PartnerLineCheckOutcome.Short, 1));
    }

    [Theory]
    [Trait("BR", "BR-CHK-02")]
    [InlineData("4990", "{\"status\":\"SOLD_OUT\"}", false, "BAGS")]
    [InlineData("4990", "{\"status\":\"AVAILABLE\"}", true, "BAGS")]
    [InlineData("null", "{\"status\":\"AVAILABLE\"}", false, "BAGS")]
    [InlineData("4990", "{\"status\":\"AVAILABLE\"}", false, "GARDEN")]
    [InlineData("4990", "{\"status\":\"SOMETHING_NEW\"}", false, "BAGS")]
    public async Task BR_CHK_02_harbor_sold_out_discontinued_unpriced_unmapped_or_unknown_stock_is_unavailable(
        string cents, string inventory, bool discontinued, string dept)
    {
        var result = await CheckHarborAsync(new StubPartner(_ => StubPartner.Json(HarborItem("HSC-1", cents, inventory, discontinued, dept))));

        result.Outcome.Should().Be(PartnerLineCheckOutcome.Unavailable);
    }

    [Fact]
    [Trait("BR", "BR-CHK-02")]
    public async Task BR_CHK_02_an_item_harbor_no_longer_knows_is_unavailable()
    {
        var result = await CheckHarborAsync(new StubPartner(_ =>
            StubPartner.Json("{\"error\":{\"code\":\"ITEM_NOT_FOUND\",\"message\":\"no\"}}", HttpStatusCode.NotFound)));

        (result.Outcome, result.AvailableQuantity).Should().Be((PartnerLineCheckOutcome.Unavailable, 0));
    }

    [Theory]
    [Trait("BR", "BR-CHK-02")]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task BR_CHK_02_harbor_errors_make_the_line_unreachable_never_sold_on_a_guess(HttpStatusCode status)
    {
        var result = await CheckHarborAsync(new StubPartner(_ => StubPartner.Status(status)));

        result.Outcome.Should().Be(PartnerLineCheckOutcome.Unreachable);
        result.Detail.Should().Contain(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait("BR", "BR-CHK-02")]
    public async Task BR_CHK_02_harbor_answering_for_another_item_is_unreachable()
    {
        var result = await CheckHarborAsync(new StubPartner(_ => StubPartner.Json(HarborItem("HSC-2"))));

        result.Outcome.Should().Be(PartnerLineCheckOutcome.Unreachable);
    }

    [Theory]
    [Trait("BR", "BR-CHK-02")]
    [InlineData("K-7731;5;", 2, PartnerLineCheckOutcome.Ok, 5)]
    [InlineData("K-7731;1;", 2, PartnerLineCheckOutcome.Short, 1)]
    [InlineData("K-7731;0;2026-12-01", 1, PartnerLineCheckOutcome.Unavailable, 0)]
    [InlineData("K-OTHER;9;", 1, PartnerLineCheckOutcome.Unavailable, null)]
    public async Task BR_CHK_02_kestrel_quantities_come_from_the_stock_csv(string row, int quantity, PartnerLineCheckOutcome outcome, int? available)
    {
        var kestrel = new StubPartner(_ => StubPartner.Text($"ref;units;restock_date\r\n{row}\r\n", "text/csv"));

        var result = await CheckKestrelAsync(kestrel, quantity);

        (result.Outcome, result.AvailableQuantity, result.CurrentUnitPriceUsd).Should().Be((outcome, available, (decimal?)null));
        var request = kestrel.Requests.Should().ContainSingle().Subject;
        (request.Uri.AbsolutePath, request.Query["refs"].ToString()).Should().Be(("/stock", "K-7731"));
        request.Header("Authorization").Should().StartWith("Basic ");
    }

    [Fact]
    [Trait("BR", "BR-CHK-02")]
    public async Task BR_CHK_02_kestrel_down_makes_its_lines_unreachable()
    {
        var result = await CheckKestrelAsync(new StubPartner(_ => StubPartner.Status(HttpStatusCode.ServiceUnavailable)), 1);

        result.Outcome.Should().Be(PartnerLineCheckOutcome.Unreachable);
    }
}
