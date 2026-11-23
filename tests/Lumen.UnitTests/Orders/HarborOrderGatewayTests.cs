using System.Net;
using System.Text.Json;
using Lumen.Api.Partners.Ports;
using Lumen.Domain.Catalog;
using static Lumen.UnitTests.Orders.OrderGatewayTestKit;

namespace Lumen.UnitTests.Orders;

/// <summary>
/// Harbor's <c>POST /v2/orders</c> through the order gateway (BR-ORD-01, BR-ORD-03), against contract-valid answers. The
/// gateway runs with the real named client: key handler, rate limiter and resilience pipeline.
/// </summary>
public sealed class HarborOrderGatewayTests
{
    private static async Task<(PartnerOrderResult Result, OrderStub Harbor)> SubmitAsync(
        Func<SentRequest, HttpResponseMessage> respond, PartnerOrderRequest? request = null)
    {
        var harbor = new OrderStub(respond);
        await using var services = Services(harbor: harbor);
        var result = await services.Gateway(ProductSources.Harbor).SubmitAsync(request ?? Request(), Ct);
        return (result, harbor);
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_sends_one_line_with_the_external_ref_and_the_ship_to_of_the_contract()
    {
        var shipTo = ShipTo with { Line2 = "Unit 4" };
        var (_, harbor) = await SubmitAsync(_ => OrderStub.Json(HttpStatusCode.Created, """{"order_id":"H-88812","status":"ACCEPTED"}"""),
            Request("LG-7K3M9QX2-3", "HSC-10021", 2, shipTo));

        var sent = harbor.Requests.Should().ContainSingle().Subject;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.Uri.AbsolutePath.Should().Be("/v2/orders");
        sent.Header("X-Harbor-Key").Should().Be(HarborKey);
        sent.ContentType.Should().StartWith("application/json");

        using var body = JsonDocument.Parse(sent.Body);
        var root = body.RootElement;
        root.GetProperty("external_ref").GetString().Should().Be("LG-7K3M9QX2-3");
        var line = root.GetProperty("lines").EnumerateArray().Should().ContainSingle().Subject;
        line.GetProperty("item_code").GetString().Should().Be("HSC-10021");
        line.GetProperty("qty").GetInt32().Should().Be(2);
        var to = root.GetProperty("ship_to");
        to.GetProperty("name").GetString().Should().Be("Ana Silva");
        to.GetProperty("line1").GetString().Should().Be("12 Harbour Road");
        to.GetProperty("line2").GetString().Should().Be("Unit 4");
        to.GetProperty("city").GetString().Should().Be("Portland");
        to.GetProperty("region").GetString().Should().Be("OR");
        to.GetProperty("postal_code").GetString().Should().Be("97201");
        to.GetProperty("country_iso2").GetString().Should().Be("US");
    }

    [Fact]
    public async Task An_empty_second_address_line_is_sent_as_null_as_in_the_contract()
    {
        var (_, harbor) = await SubmitAsync(_ => OrderStub.Json(HttpStatusCode.Created, """{"order_id":"H-1","status":"ACCEPTED"}"""));

        using var body = JsonDocument.Parse(harbor.Requests.Single().Body);
        body.RootElement.GetProperty("ship_to").GetProperty("line2").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_201_confirms_the_line_with_harbors_order_id()
    {
        var (result, _) = await SubmitAsync(_ => OrderStub.Json(HttpStatusCode.Created, """{"order_id":"H-88812","status":"ACCEPTED"}"""));

        result.Outcome.Should().Be(PartnerOrderOutcome.Confirmed);
        result.PartnerOrderId.Should().Be("H-88812");
        result.AlreadyExisted.Should().BeFalse();
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_200_for_a_used_reference_is_the_original_order_not_an_error()
    {
        var (result, _) = await SubmitAsync(_ => OrderStub.Json(HttpStatusCode.OK, """{"order_id":"H-88812","status":"ACCEPTED"}"""));

        result.Outcome.Should().Be(PartnerOrderOutcome.Confirmed);
        result.PartnerOrderId.Should().Be("H-88812");
        result.AlreadyExisted.Should().BeTrue();
    }

    [Theory]
    [Trait("BR", "BR-ORD-01")]
    [InlineData(HttpStatusCode.Created, "{}", "application/json")]
    [InlineData(HttpStatusCode.Created, "{\"order_id\":null,\"status\":\"ACCEPTED\"}", "application/json")]
    [InlineData(HttpStatusCode.Created, "ok", "text/plain")]
    [InlineData(HttpStatusCode.Created, "", "application/json")]
    [InlineData(HttpStatusCode.OK, "<html>Order received</html>", "text/html")]
    public async Task BR_ORD_01_a_201_or_200_without_a_readable_order_id_still_confirms_the_line(HttpStatusCode status, string body, string contentType)
    {
        // The status is Harbor's decision: it has the order and reduced its stock. Reading it as a rejection would
        // refund goods Harbor is shipping; the order id is only kept for reference.
        var (result, _) = await SubmitAsync(_ => new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, contentType) });

        (result.Outcome, result.PartnerOrderId, result.AlreadyExisted).Should().Be((PartnerOrderOutcome.Confirmed, (string?)null, status == HttpStatusCode.OK));
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_400_invalid_request_rejects_the_line_with_harbors_message()
    {
        var (result, harbor) = await SubmitAsync(_ => OrderStub.Json(HttpStatusCode.BadRequest,
            """{"error":{"code":"INVALID_REQUEST","message":"ship_to.postal_code is required"}}"""));

        (result.Outcome, result.Reason, result.HttpStatus)
            .Should().Be((PartnerOrderOutcome.Rejected, "INVALID_REQUEST: ship_to.postal_code is required", 400));
        harbor.Requests.Should().ContainSingle("a POST is never repeated by the pipeline");
    }

    [Fact]
    public async Task Order_ids_are_opaque_a_number_or_an_unexpected_status_is_kept_as_harbor_sent_it()
    {
        var (result, _) = await SubmitAsync(_ => OrderStub.Json(HttpStatusCode.Created, """{"order_id":9912,"status":"QUEUED"}"""));

        result.Outcome.Should().Be(PartnerOrderOutcome.Confirmed);
        result.PartnerOrderId.Should().Be("9912");
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_409_insufficient_stock_rejects_with_what_harbor_has()
    {
        var (result, _) = await SubmitAsync(_ => OrderStub.Json(HttpStatusCode.Conflict,
            """{"error":{"code":"INSUFFICIENT_STOCK","message":"Not enough stock","item_code":"HSC-10021","available":1}}"""));

        result.Outcome.Should().Be(PartnerOrderOutcome.Rejected);
        result.Reason.Should().Be("INSUFFICIENT_STOCK: 1 available");
        result.AvailableQuantity.Should().Be(1);
        result.HttpStatus.Should().Be(409);
        result.OutcomeUnknown.Should().BeFalse("Harbor answers a used external_ref with the original order, so a rejection is final");
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_422_invalid_item_rejects_with_harbors_code_and_message()
    {
        var (result, _) = await SubmitAsync(_ => OrderStub.Json(HttpStatusCode.UnprocessableEntity,
            """{"error":{"code":"INVALID_ITEM","message":"Item HSC-10021 is discontinued."}}"""));

        result.Outcome.Should().Be(PartnerOrderOutcome.Rejected);
        result.Reason.Should().Be("INVALID_ITEM: Item HSC-10021 is discontinued.");
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_429_retries_later_after_retry_after_and_the_post_is_not_repeated_by_the_client()
    {
        var (result, harbor) = await SubmitAsync(_ =>
        {
            var response = OrderStub.Json(HttpStatusCode.TooManyRequests, """{"error":{"code":"RATE_LIMITED","message":"Slow down"}}""");
            response.Headers.Add("Retry-After", "12");
            return response;
        });

        result.Outcome.Should().Be(PartnerOrderOutcome.RetryLater);
        result.RetryAfter.Should().Be(TimeSpan.FromSeconds(12));
        result.OutcomeUnknown.Should().BeFalse("a 429 is refused before anything is ordered");
        harbor.Requests.Should().ContainSingle("the job resends the order itself, with the same external_ref");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [Trait("BR", "BR-ORD-01")]
    [Trait("BR", "BR-ORD-04")]
    public async Task BR_ORD_01_a_failed_send_retries_later_and_is_never_repeated_inside_the_http_pipeline(HttpStatusCode status, bool outcomeUnknown)
    {
        var (result, harbor) = await SubmitAsync(_ => OrderStub.Empty(status));

        result.Outcome.Should().Be(PartnerOrderOutcome.RetryLater);
        result.OutcomeUnknown.Should().Be(outcomeUnknown, "a 5xx may have created the order; a refusal cannot have");
        result.HttpStatus.Should().Be((int)status);
        result.Error.Should().Contain(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
        harbor.Requests.Should().ContainSingle();
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_a_lost_response_retries_later_because_harbor_may_have_the_order()
    {
        var (result, harbor) = await SubmitAsync(_ => throw new HttpRequestException("Connection reset by peer"));

        result.Outcome.Should().Be(PartnerOrderOutcome.RetryLater);
        result.Error.Should().Contain("Connection reset");
        result.OutcomeUnknown.Should().BeTrue("the request may have been processed before the connection broke");
        harbor.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.NameResolutionError)]
    [Trait("BR", "BR-ORD-04")]
    public async Task BR_ORD_04_a_send_that_never_reached_harbor_is_not_an_unknown_outcome(HttpRequestError error)
    {
        var (result, _) = await SubmitAsync(_ => throw new HttpRequestException(error, "No connection could be made."));

        result.Outcome.Should().Be(PartnerOrderOutcome.RetryLater);
        result.OutcomeUnknown.Should().BeFalse();
    }

    [Fact]
    [Trait("BR", "BR-ORD-04")]
    public async Task BR_ORD_04_a_timed_out_send_is_an_unknown_outcome()
    {
        var (result, _) = await SubmitAsync(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."));

        result.Outcome.Should().Be(PartnerOrderOutcome.RetryLater);
        result.OutcomeUnknown.Should().BeTrue();
    }

    [Fact]
    public async Task Harbor_has_no_status_endpoint()
    {
        await using var services = Services(harbor: new OrderStub(_ => OrderStub.Empty(HttpStatusCode.NotFound)));
        var gateway = services.Gateway(ProductSources.Harbor);

        gateway.DecidesLater.Should().BeFalse();
        await FluentActions.Awaiting(() => gateway.GetStatusAsync("H-1", Ct)).Should().ThrowAsync<NotSupportedException>();
    }
}
