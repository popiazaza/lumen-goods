using System.Net;
using System.Text;
using System.Xml.Linq;

namespace KestrelOutfitters.StandIn.Tests;

/// <summary>Contract: POST /orders and GET /orders/{ticket}, including the asynchronous decision.</summary>
public sealed class OrderTests(KestrelStandInFactory factory) : IClassFixture<KestrelStandInFactory>
{
    private static readonly TimeSpan JustBeforeEarliestDecision = TimeSpan.FromSeconds(9.9);
    private static readonly TimeSpan PastLatestDecision = TimeSpan.FromSeconds(31);

    [Fact]
    public async Task Valid_order_gets_202_with_a_received_ack_and_a_ticket()
    {
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsync("/orders", Order(NewReference(), ("K-7781", 1)), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/xml");
        body.Should().StartWith("<OrderAck ");
        var ack = XElement.Parse(body);
        ack.Attributes().Select(a => a.Name.LocalName).Should().Equal("status", "ticket");
        ((string)ack.Attribute("status")!).Should().Be("RECEIVED");
        ((string)ack.Attribute("ticket")!).Should().MatchRegex("^KT-[0-9]+$");
    }

    [Fact]
    public async Task Reused_reference_gets_409_duplicate_reference_with_the_original_ticket()
    {
        using var client = factory.CreateAuthenticatedClient();
        var reference = NewReference();
        var ticket = await PlaceAsync(client, reference, ("K-7781", 1));

        using var response = await client.PostAsync("/orders", Order(reference, ("K-7784", 2)), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/xml");
        var error = XElement.Parse(body);
        error.Name.LocalName.Should().Be("Error");
        error.Attributes().Select(a => a.Name.LocalName).Should().Equal("code", "ticket");
        ((string)error.Attribute("code")!).Should().Be("DUPLICATE_REFERENCE");
        ((string)error.Attribute("ticket")!).Should().Be(ticket);
    }

    public static TheoryData<string, string, string> InvalidRequests => new()
    {
        { "unknown ref", "application/xml", OrderXml("R-unknown", ("K-0000", 1)) },
        { "qty zero", "application/xml", OrderXml("R-zero", ("K-7781", 0)) },
        { "qty negative", "application/xml", OrderXml("R-negative", ("K-7781", -1)) },
        { "qty not a number", "application/xml", OrderXml("R-nan", ("K-7781", 1)).Replace("qty=\"1\"", "qty=\"two\"", StringComparison.Ordinal) },
        { "no lines", "application/xml", OrderXml("R-empty") },
        { "missing reference", "application/xml", OrderXml("R-x", ("K-7781", 1)).Replace(" reference=\"R-x\"", "", StringComparison.Ordinal) },
        { "blank reference", "application/xml", OrderXml(" ", ("K-7781", 1)) },
        { "missing city", "application/xml", OrderXml("R-city", ("K-7781", 1)).Replace("<City>Portland</City>", "", StringComparison.Ordinal) },
        { "empty postcode", "application/xml", OrderXml("R-post", ("K-7781", 1)).Replace("<Postcode>97201</Postcode>", "<Postcode> </Postcode>", StringComparison.Ordinal) },
        { "no delivery", "application/xml", "<PurchaseOrder reference=\"R-nodel\"><Line ref=\"K-7781\" qty=\"1\"/></PurchaseOrder>" },
        { "wrong root", "application/xml", "<Order reference=\"R-root\"/>" },
        { "malformed xml", "application/xml", "<PurchaseOrder reference=\"R-bad\"><Line" },
        { "empty body", "application/xml", "" },
        { "dtd", "application/xml", "<!DOCTYPE PurchaseOrder [<!ENTITY x \"y\">]><PurchaseOrder reference=\"&x;\"/>" },
        { "json content type", "application/json", OrderXml("R-json", ("K-7781", 1)) },
        { "text content type", "text/plain", OrderXml("R-text", ("K-7781", 1)) },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Invalid_order_gets_400_invalid_request_with_a_message(string because, string contentType, string body)
    {
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsync("/orders", new StringContent(body, Encoding.UTF8, contentType), TestContext.Current.CancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/xml");
        var error = XElement.Parse(responseBody);
        error.Name.LocalName.Should().Be("Error");
        error.Attributes().Select(a => a.Name.LocalName).Should().Equal("code", "message");
        ((string)error.Attribute("code")!).Should().Be("INVALID_REQUEST");
        ((string)error.Attribute("message")!).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Rejected_request_does_not_use_up_the_reference()
    {
        using var client = factory.CreateAuthenticatedClient();
        var reference = NewReference();

        using var invalid = await client.PostAsync("/orders", Order(reference, ("K-0000", 1)), TestContext.Current.CancellationToken);
        using var valid = await client.PostAsync("/orders", Order(reference, ("K-7781", 1)), TestContext.Current.CancellationToken);

        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        valid.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Unknown_ticket_gets_404_with_an_empty_body()
    {
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/orders/KT-0", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Order_stays_received_for_10_seconds_then_is_confirmed_and_stock_is_reduced()
    {
        using var client = factory.CreateAuthenticatedClient();
        await SetStockAsync("K-7761", 5);
        var ticket = await PlaceAsync(client, NewReference(), ("K-7761", 2));

        (await GetStatusAsync(client, ticket)).Should().Be($"<OrderStatus ticket=\"{ticket}\" state=\"RECEIVED\" />");
        factory.Time.Advance(JustBeforeEarliestDecision);
        (await GetStatusAsync(client, ticket)).Should().Contain("state=\"RECEIVED\"");
        (await GetUnitsAsync(client, "K-7761")).Should().Be("5", "stock is not held while RECEIVED");

        factory.Time.Advance(PastLatestDecision - JustBeforeEarliestDecision);

        var status = XElement.Parse(await GetStatusAsync(client, ticket));
        status.Name.LocalName.Should().Be("OrderStatus");
        ((string)status.Attribute("ticket")!).Should().Be(ticket);
        ((string)status.Attribute("state")!).Should().Be("CONFIRMED");
        status.Attribute("reason").Should().BeNull();
        (await GetUnitsAsync(client, "K-7761")).Should().Be("3");
    }

    [Fact]
    public async Task Order_exceeding_stock_is_rejected_with_the_ref_and_stock_is_unchanged()
    {
        using var client = factory.CreateAuthenticatedClient();
        await SetStockAsync("K-7762", 1);
        var ticket = await PlaceAsync(client, NewReference(), ("K-7762", 2));

        factory.Time.Advance(PastLatestDecision);

        (await GetStatusAsync(client, ticket))
            .Should().Be($"<OrderStatus ticket=\"{ticket}\" state=\"REJECTED\" reason=\"OUT_OF_STOCK:K-7762\" />");
        (await GetUnitsAsync(client, "K-7762")).Should().Be("1");
    }

    [Fact]
    public async Task Stock_is_checked_at_decision_time_not_at_submission()
    {
        using var client = factory.CreateAuthenticatedClient();
        await SetStockAsync("K-7772", 5);
        var ticket = await PlaceAsync(client, NewReference(), ("K-7772", 1));

        await SetStockAsync("K-7772", 0);
        factory.Time.Advance(PastLatestDecision);

        (await GetStatusAsync(client, ticket)).Should().Contain("state=\"REJECTED\" reason=\"OUT_OF_STOCK:K-7772\"");
    }

    [Fact]
    public async Task Whole_order_is_rejected_naming_the_first_short_line_and_no_stock_moves()
    {
        using var client = factory.CreateAuthenticatedClient();
        await SetStockAsync("K-7741", 50);
        await SetStockAsync("K-7742", 0);
        await SetStockAsync("K-7743", 0);
        var ticket = await PlaceAsync(client, NewReference(), ("K-7741", 1), ("K-7742", 1), ("K-7743", 1));

        factory.Time.Advance(PastLatestDecision);

        (await GetStatusAsync(client, ticket)).Should().Contain("reason=\"OUT_OF_STOCK:K-7742\"");
        (await GetUnitsAsync(client, "K-7741")).Should().Be("50");
    }

    [Fact]
    public async Task Repeated_lines_for_one_ref_are_summed()
    {
        using var client = factory.CreateAuthenticatedClient();
        await SetStockAsync("K-7791", 3);
        var ticket = await PlaceAsync(client, NewReference(), ("K-7791", 2), ("K-7791", 2));

        factory.Time.Advance(PastLatestDecision);

        (await GetStatusAsync(client, ticket)).Should().Contain("reason=\"OUT_OF_STOCK:K-7791\"");
    }

    [Fact]
    public async Task Inactive_product_is_accepted_then_rejected_as_out_of_stock()
    {
        using var client = factory.CreateAuthenticatedClient();
        await SetStockAsync("K-7792", 10);
        (await factory.RunCliAsync("set-active", "K-7792", "N")).ExitCode.Should().Be(0);
        var ticket = await PlaceAsync(client, NewReference(), ("K-7792", 1));

        factory.Time.Advance(PastLatestDecision);

        (await GetStatusAsync(client, ticket)).Should().Contain("state=\"REJECTED\" reason=\"OUT_OF_STOCK:K-7792\"");
        (await GetUnitsAsync(client, "K-7792")).Should().Be("10");
    }

    [Fact]
    public async Task Decision_is_reflected_in_stock_even_if_nobody_polls_the_ticket()
    {
        using var client = factory.CreateAuthenticatedClient();
        await SetStockAsync("K-7801", 4);
        await PlaceAsync(client, NewReference(), ("K-7801", 4));

        factory.Time.Advance(PastLatestDecision);

        (await GetUnitsAsync(client, "K-7801")).Should().Be("0");
    }

    private static string NewReference() => $"LG-{Guid.NewGuid():N}";

    private static StringContent Order(string reference, params (string Ref, int Qty)[] lines) =>
        new(OrderXml(reference, lines), Encoding.UTF8, "application/xml");

    private static string OrderXml(string reference, params (string Ref, int Qty)[] lines) =>
        new XElement("PurchaseOrder",
            new XAttribute("reference", reference),
            lines.Select(l => new XElement("Line", new XAttribute("ref", l.Ref), new XAttribute("qty", l.Qty))),
            new XElement("Delivery",
                new XElement("Name", "Ana Silva"),
                new XElement("Street", "12 Harbour Road"),
                new XElement("City", "Portland"),
                new XElement("Postcode", "97201"),
                new XElement("Country", "US"))).ToString(SaveOptions.DisableFormatting);

    private static async Task<string> PlaceAsync(HttpClient client, string reference, params (string Ref, int Qty)[] lines)
    {
        using var response = await client.PostAsync("/orders", Order(reference, lines), TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        return (string)XElement.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Attribute("ticket")!;
    }

    private static async Task<string> GetStatusAsync(HttpClient client, string ticket)
    {
        using var response = await client.GetAsync($"/orders/{ticket}", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/xml");
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> GetUnitsAsync(HttpClient client, string reference)
    {
        var csv = await client.GetStringAsync($"/stock?refs={reference}", TestContext.Current.CancellationToken);
        return csv.Split("\r\n")[1].Split(';')[1];
    }

    private async Task SetStockAsync(string reference, int units) =>
        (await factory.RunCliAsync("set-stock", reference, units.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ExitCode.Should().Be(0);
}
