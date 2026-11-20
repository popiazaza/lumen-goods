using System.Net;
using System.Text;
using System.Xml.Linq;
using Lumen.Api.Partners.Ports;
using Lumen.Domain.Catalog;
using static Lumen.UnitTests.Orders.OrderGatewayTestKit;

namespace Lumen.UnitTests.Orders;

/// <summary>
/// Kestrel's asynchronous ordering through the order gateway (BR-ORD-01, BR-ORD-03): the PurchaseOrder built with
/// XmlWriter, the 202/409/400 answers, and the polled decision, parsed safely. Tickets are opaque.
/// </summary>
public sealed class KestrelOrderGatewayTests
{
    private static async Task<(PartnerOrderResult Result, OrderStub Kestrel)> SubmitAsync(
        Func<SentRequest, HttpResponseMessage> respond, PartnerOrderRequest? request = null)
    {
        var kestrel = new OrderStub(respond);
        await using var services = Services(kestrel: kestrel);
        var result = await services.Gateway(ProductSources.Kestrel).SubmitAsync(request ?? Request(item: "K-7731"), Ct);
        return (result, kestrel);
    }

    private static async Task<(PartnerOrderResult Result, OrderStub Kestrel)> PollAsync(string ticket, Func<SentRequest, HttpResponseMessage> respond)
    {
        var kestrel = new OrderStub(respond);
        await using var services = Services(kestrel: kestrel);
        var result = await services.Gateway(ProductSources.Kestrel).GetStatusAsync(ticket, Ct);
        return (result, kestrel);
    }

    private static HttpResponseMessage Ack(string ticket) =>
        OrderStub.Xml(HttpStatusCode.Accepted, $"""<OrderAck status="RECEIVED" ticket="{ticket}"/>""");

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_the_purchase_order_is_the_contracts_xml_with_street_as_line1_line2_and_no_region()
    {
        var shipTo = new PartnerShipTo("Ana \"Ace\" Silva & Sons", "12 <Harbour> Road", "Apt 3", "Portland", "OR", "97201", "US");
        var (_, kestrel) = await SubmitAsync(_ => Ack("KT-5521"), Request("LG-7K3M9QX2-2", "K-7731", 2, shipTo));

        var sent = kestrel.Requests.Should().ContainSingle().Subject;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.Uri.AbsolutePath.Should().Be("/orders");
        sent.ContentType.Should().StartWith("application/xml");
        sent.Header("Authorization").Should().Be("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{KestrelUser}:{KestrelPassword}")));

        // Escaped, never concatenated: the document parses and carries the text exactly.
        var order = XDocument.Parse(sent.Body).Root!;
        order.Name.LocalName.Should().Be("PurchaseOrder");
        order.Attribute("reference")!.Value.Should().Be("LG-7K3M9QX2-2");
        var line = order.Elements("Line").Should().ContainSingle().Subject;
        line.Attribute("ref")!.Value.Should().Be("K-7731");
        line.Attribute("qty")!.Value.Should().Be("2");
        var delivery = order.Element("Delivery")!;
        delivery.Element("Name")!.Value.Should().Be("Ana \"Ace\" Silva & Sons");
        delivery.Element("Street")!.Value.Should().Be("12 <Harbour> Road, Apt 3");
        delivery.Element("City")!.Value.Should().Be("Portland");
        delivery.Element("Postcode")!.Value.Should().Be("97201");
        delivery.Element("Country")!.Value.Should().Be("US");
        delivery.Elements().Select(e => e.Name.LocalName).Should().Equal("Name", "Street", "City", "Postcode", "Country");
        sent.Body.Should().Contain("&amp;").And.Contain("&lt;Harbour&gt;");
    }

    [Fact]
    public async Task Without_a_second_line_the_street_is_line1_and_control_characters_are_dropped()
    {
        var shipTo = new PartnerShipTo("Ana\u0001 Silva", "12 Harbour Road", " ", "Portland", "OR", "97201", "US");
        var (_, kestrel) = await SubmitAsync(_ => Ack("KT-1"), Request(item: "K-7731", shipTo: shipTo));

        var delivery = XDocument.Parse(kestrel.Requests.Single().Body).Root!.Element("Delivery")!;
        delivery.Element("Street")!.Value.Should().Be("12 Harbour Road");
        delivery.Element("Name")!.Value.Should().Be("Ana Silva");
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_202_received_is_accepted_with_its_ticket_whatever_the_ticket_looks_like()
    {
        var (result, _) = await SubmitAsync(_ => Ack("7f3a/9 b+c"));

        result.Outcome.Should().Be(PartnerOrderOutcome.Accepted);
        result.PartnerOrderId.Should().Be("7f3a/9 b+c");
        result.AlreadyExisted.Should().BeFalse();
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_409_duplicate_reference_adopts_the_ticket_of_the_order_kestrel_already_has()
    {
        var (result, _) = await SubmitAsync(_ => OrderStub.Xml(HttpStatusCode.Conflict, """<Error code="DUPLICATE_REFERENCE" ticket="KT-5521"/>"""));

        result.Outcome.Should().Be(PartnerOrderOutcome.Accepted);
        result.PartnerOrderId.Should().Be("KT-5521");
        result.AlreadyExisted.Should().BeTrue();
    }

    [Fact]
    public async Task A_409_without_a_ticket_or_a_202_without_one_is_resent_later()
    {
        (await SubmitAsync(_ => OrderStub.Xml(HttpStatusCode.Conflict, """<Error code="DUPLICATE_REFERENCE"/>"""))).Result.Outcome
            .Should().Be(PartnerOrderOutcome.RetryLater);
        (await SubmitAsync(_ => OrderStub.Xml(HttpStatusCode.Accepted, """<OrderAck status="RECEIVED"/>"""))).Result.Outcome
            .Should().Be(PartnerOrderOutcome.RetryLater);
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_400_invalid_request_rejects_with_kestrels_message()
    {
        var (result, _) = await SubmitAsync(_ => OrderStub.Xml(HttpStatusCode.BadRequest, """<Error code="INVALID_REQUEST" message="Unknown ref: K-7731"/>"""));

        result.Outcome.Should().Be(PartnerOrderOutcome.Rejected);
        result.Reason.Should().Be("INVALID_REQUEST: Unknown ref: K-7731");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_a_failed_send_retries_later_and_is_never_repeated_inside_the_http_pipeline(HttpStatusCode status)
    {
        var (result, kestrel) = await SubmitAsync(_ => OrderStub.Empty(status));

        result.Outcome.Should().Be(PartnerOrderOutcome.RetryLater);
        kestrel.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData("RECEIVED", null, PartnerOrderOutcome.Accepted, null)]
    [InlineData("CONFIRMED", null, PartnerOrderOutcome.Confirmed, null)]
    [InlineData("REJECTED", "OUT_OF_STOCK:K-7731", PartnerOrderOutcome.Rejected, "OUT_OF_STOCK:K-7731")]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_the_polled_state_maps_to_the_answer(string state, string? reason, PartnerOrderOutcome expected, string? expectedReason)
    {
        var reasonAttribute = reason is null ? "" : $" reason=\"{reason}\"";
        var (result, kestrel) = await PollAsync("KT-5521",
            _ => OrderStub.Xml(HttpStatusCode.OK, $"""<OrderStatus ticket="KT-5521" state="{state}"{reasonAttribute}/>"""));

        result.Outcome.Should().Be(expected);
        result.Reason.Should().Be(expectedReason);
        kestrel.Requests.Single().Uri.AbsolutePath.Should().Be("/orders/KT-5521");
    }

    [Fact]
    public async Task An_opaque_ticket_is_escaped_as_one_path_segment()
    {
        var (_, kestrel) = await PollAsync("7f3a/9 b",
            _ => OrderStub.Xml(HttpStatusCode.OK, """<OrderStatus ticket="7f3a/9 b" state="RECEIVED"/>"""));

        kestrel.Requests.Single().Uri.AbsoluteUri.Should().EndWith("/orders/7f3a%2F9%20b");
    }

    [Fact]
    public async Task A_ticket_kestrel_does_not_know_is_not_found()
    {
        var (result, _) = await PollAsync("KT-404", _ => OrderStub.Empty(HttpStatusCode.NotFound));

        result.Outcome.Should().Be(PartnerOrderOutcome.NotFound);
    }

    [Theory]
    [InlineData("""<OrderStatus ticket="KT-5521" state="SHIPPED"/>""")]
    [InlineData("""<OrderStatus ticket="KT-9999" state="CONFIRMED"/>""")]
    [InlineData("""<OrderAck status="RECEIVED" ticket="KT-5521"/>""")]
    [InlineData("not xml")]
    [InlineData("")]
    public async Task An_answer_lumen_cannot_read_as_a_decision_is_asked_again_later(string body)
    {
        var (result, _) = await PollAsync("KT-5521", _ => OrderStub.Xml(HttpStatusCode.OK, body));

        result.Outcome.Should().Be(PartnerOrderOutcome.RetryLater);
    }

    [Fact]
    public async Task A_dtd_in_an_answer_is_refused_never_expanded()
    {
        const string BillionLaughs = """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [<!ENTITY lol "lol"><!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">]>
            <OrderStatus ticket="KT-5521" state="CONFIRMED" reason="&lol2;"/>
            """;
        var (result, _) = await PollAsync("KT-5521", _ => OrderStub.Xml(HttpStatusCode.OK, BillionLaughs));

        result.Outcome.Should().Be(PartnerOrderOutcome.RetryLater);
    }

    [Fact]
    public async Task A_failed_poll_retries_later()
    {
        var (result, _) = await PollAsync("KT-5521", _ => OrderStub.Empty(HttpStatusCode.InternalServerError));

        result.Outcome.Should().Be(PartnerOrderOutcome.RetryLater);
        result.HttpStatus.Should().Be(500);
    }
}
