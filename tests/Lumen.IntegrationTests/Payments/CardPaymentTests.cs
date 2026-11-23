using System.Net;
using System.Text.Json;
using Lumen.Api.Catalog;
using Lumen.Api.Orders;
using Lumen.Api.Payments;
using Lumen.Domain.Catalog;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Lumen.IntegrationTests.Catalog;
using Lumen.IntegrationTests.Checkout;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lumen.IntegrationTests.Payments;

/// <summary>
/// Card payments end to end on the real API and database with the in-process mock gateway (BR-PAY-01..05, BR-PAY-07,
/// BR-CHK-01, BR-STOCK-01, BR-ORD-01). Charges run detached from the request; tests wait for them explicitly and move
/// the fake clock for the time-based cards.
/// </summary>
public sealed class CardPaymentTests(CheckoutHost host) : IClassFixture<CheckoutHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Shopper Shopper, OrderDetailDto Order)> OrderAsync(decimal price = 18.50m, int quantity = 1)
    {
        var shopper = await host.NewShopperAsync();
        await shopper.AddToCartAsync(await host.OwnProductAsync(price), quantity);
        return (shopper, await shopper.PlaceOrderAsync());
    }

    private static async Task<PaymentStatusDto> AcceptedAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadAsync<PaymentStatusDto>();
    }

    [Theory]
    [Trait("BR", "BR-PAY-02")]
    [Trait("BR", "BR-PAY-05")]
    [InlineData("4111 1111 1111 1111", PaymentStatus.Succeeded, null)]
    [InlineData("5555 5555 5555 4444", PaymentStatus.Succeeded, null)]
    [InlineData("4242 0000 0000 0018", PaymentStatus.Declined, "Insufficient funds")]
    [InlineData("4242 0000 0000 0026", PaymentStatus.Declined, "Your card was declined")]
    public async Task BR_PAY_02_each_test_card_gives_its_result(string card, PaymentStatus expected, string? customerMessage)
    {
        var (shopper, order) = await OrderAsync();

        var payment = await shopper.PayByCardAndWaitAsync(order.Number, card);

        payment.Status.Should().Be(expected);
        payment.DeclineMessage.Should().Be(customerMessage);
        if (expected == PaymentStatus.Succeeded)
        {
            // Own items only: confirmed at once, so the order is complete.
            (payment.OrderStatus, payment.RetryMode).Should().Be((OrderStatus.Completed, PaymentRetryMode.None));
        }
        else
        {
            (payment.OrderStatus, payment.AttemptsLeft, payment.RetryMode, payment.CanRetry)
                .Should().Be((OrderStatus.AwaitingPayment, 4, PaymentRetryMode.AnyMethod, true));
        }

        // BR-PAY-05 scenario 2 (0018): a decline is never retried automatically, so the gateway got exactly one call.
        (await host.GatewayChargesAsync((await host.OrderAsync(order.Number)).Id)).Should().ContainSingle();
        host.Gateway.ChargeCalls(payment.PaymentId).Should().Be(1, "a decline is never retried automatically (BR-PAY-05)");
        (await host.EventsAsync(payment.PaymentId)).Should().ContainSingle(e => e.Type == PaymentEventType.SentToGateway)
            .Which.Detail.Should().Be("First try");
    }

    [Fact]
    [Trait("BR", "BR-PAY-07")]
    public async Task BR_PAY_07_a_stolen_card_shows_the_customer_a_generic_decline_and_the_admin_the_reason()
    {
        var (shopper, order) = await OrderAsync();

        var payment = await shopper.PayByCardAndWaitAsync(order.Number, "4242000000000026");

        payment.DeclineMessage.Should().Be(DeclineMessages.GenericDecline);
        (await host.EventsAsync(payment.PaymentId)).Should().ContainSingle(e => e.Type == PaymentEventType.Declined)
            .Which.Detail.Should().Be("Card reported stolen");
    }

    [Fact]
    [Trait("BR", "BR-PAY-02")]
    public async Task BR_PAY_02_after_a_decline_the_order_can_be_paid_with_another_card()
    {
        var (shopper, order) = await OrderAsync();
        (await shopper.PayByCardAndWaitAsync(order.Number, "4242000000000018")).Status.Should().Be(PaymentStatus.Declined);

        var second = await shopper.PayByCardAndWaitAsync(order.Number, "4111111111111111");

        second.Status.Should().Be(PaymentStatus.Succeeded);
        (await host.OrderAsync(order.Number)).Payments.Select(p => p.Status).Should().BeEquivalentTo([PaymentStatus.Declined, PaymentStatus.Succeeded]);
    }

    [Fact]
    [Trait("BR", "BR-PAY-02")]
    public async Task BR_PAY_02_a_card_failing_luhn_is_a_validation_error_and_never_reaches_the_gateway()
    {
        var (shopper, order) = await OrderAsync();

        var response = await shopper.PayByCardAsync(order.Number, "4111 1111 1111 1112", cvc: "12");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("errors").EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("card.number", "card.cvc");
        var saved = await host.OrderAsync(order.Number);
        (saved.Status, saved.FailedPaymentAttempts, saved.Payments.Count).Should().Be((OrderStatus.AwaitingPayment, 0, 0));
        (await host.GatewayChargesAsync(saved.Id)).Should().BeEmpty();
    }

    [Fact]
    [Trait("BR", "BR-PAY-02")]
    public async Task BR_PAY_02_only_the_brand_and_last_four_digits_are_stored()
    {
        var (shopper, order) = await OrderAsync();

        var payment = await shopper.PayByCardAndWaitAsync(order.Number, "4111111111111111");

        (payment.CardBrand, payment.CardLast4).Should().Be((CardBrand.Visa, "1111"));
        var events = await host.EventsAsync(payment.PaymentId);
        events.Select(e => e.Detail ?? "").Should().NotContain(d => d.Contains("411111111111"));
        var stored = await host.WithDbAsync(db => db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.PaymentId, Ct));
        JsonSerializer.Serialize(stored).Should().NotContain("411111111111").And.NotContain("\"123\"");
    }

    [Fact]
    [Trait("BR", "BR-CHK-01")]
    public async Task BR_CHK_01_the_gateway_is_charged_the_order_total_never_an_amount_from_the_client()
    {
        var (shopper, order) = await OrderAsync(price: 40.00m);
        var body = new
        {
            method = "card",
            amount = 1.00m,
            card = new { number = "4111111111111111", name = "Ada Lovelace", expiryMonth = 12, expiryYear = host.Clock.GetUtcNow().Year + 1, cvc = "123" },
        };

        var response = await CheckoutHost.SendJsonAsync(shopper.Session, HttpMethod.Post, $"/api/v1/orders/{order.Number}/payments", body);
        (await AcceptedAsync(response)).Amount.Should().Be(order.Totals.Total);
        await host.ChargesFinishedAsync();

        (await host.GatewayChargesAsync((await host.OrderAsync(order.Number)).Id)).Should().ContainSingle().Which.Amount.Should().Be(order.Totals.Total);
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    [Trait("BR", "BR-STOCK-01")]
    public async Task BR_ORD_01_a_paid_order_sells_its_stock_confirms_own_lines_and_writes_one_partner_order_per_partner_line()
    {
        var shopper = await host.NewShopperAsync();
        var mug = await host.OwnProductAsync(18.50m, onHand: 6);
        var socks = await host.KestrelProductAsync();
        var tote = await host.HarborProductAsync();
        var other = await host.OwnProductAsync();
        await shopper.AddToCartAsync(mug, 2);
        await shopper.AddToCartAsync(socks, 1);
        await shopper.AddToCartAsync(tote, 1);
        var order = await shopper.PlaceOrderAsync();
        await shopper.AddToCartAsync(other, 1); // added after placing: not bought, stays in the cart

        (await shopper.PayByCardAndWaitAsync(order.Number, "4111111111111111")).OrderStatus.Should().Be(OrderStatus.Paid);

        var paid = await host.OrderAsync(order.Number);
        paid.PaidUtc.Should().NotBeNull();
        paid.Lines.Single(l => l.Source == ProductSources.Lumen).Status.Should().Be(OrderLineStatus.Confirmed);
        paid.Lines.Where(l => l.Source != ProductSources.Lumen).Should().AllSatisfy(l =>
        {
            l.Status.Should().Be(OrderLineStatus.AwaitingPartner);
            l.PartnerOrder!.Status.Should().Be(PartnerOrderStatus.Pending);
            l.PartnerOrder.ExternalRef.Should().Be($"{order.Number}-{l.LineNumber}");
            l.PartnerOrder.Source.Should().Be(l.Source);
        });
        var stock = await host.ProductAsync(mug.Id);
        (stock.OnHand, stock.Reserved).Should().Be((4, 0));
        var cart = await host.WithDbAsync(db => db.CartLines.Where(l => db.Carts.Any(c => c.Id == l.CartId && c.CustomerId == shopper.Id))
            .Select(l => l.ProductId).ToListAsync(Ct));
        cart.Should().Equal(other.Id);
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_a_declined_payment_writes_no_partner_order()
    {
        var shopper = await host.NewShopperAsync();
        await shopper.AddToCartAsync(await host.HarborProductAsync(), 1);
        await shopper.AddToCartAsync(await host.KestrelProductAsync(), 1);
        var order = await shopper.PlaceOrderAsync();

        (await shopper.PayByCardAndWaitAsync(order.Number, "4242000000000018")).Status.Should().Be(PaymentStatus.Declined);

        var saved = await host.OrderAsync(order.Number);
        saved.Lines.Should().OnlyContain(l => l.PartnerOrder == null && l.Status == OrderLineStatus.Pending);
    }

    [Fact]
    [Trait("BR", "BR-PAY-01")]
    public async Task BR_PAY_01_a_paid_order_cannot_be_paid_again()
    {
        var (shopper, order) = await OrderAsync();
        await shopper.PayByCardAndWaitAsync(order.Number, "4111111111111111");

        var again = await shopper.PayByWalletAsync(order.Number);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await again.ReadJsonAsync()).GetProperty("type").GetString().Should().Be(PaymentProblemTypes.OrderNotPayable);
        (await host.OrderAsync(order.Number)).Payments.Should().ContainSingle();
    }

    [Fact]
    [Trait("BR", "BR-ADM-01")]
    public async Task BR_ADM_01_another_customer_cannot_pay_read_or_complete_a_payment_of_my_order()
    {
        var (owner, order) = await OrderAsync();
        var other = await host.NewShopperAsync();
        var otherBalance = await other.WalletBalanceAsync();

        // Nothing started yet: paying it by wallet or card, or reading its payment, is 404 as for an unknown number.
        (await other.PayByWalletAsync(order.Number)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.PayByCardAsync(order.Number, "4111111111111111")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.Session.GetAsync($"/api/v1/orders/{order.Number}/payments/current")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await host.ChargesFinishedAsync();
        var untouched = await host.OrderAsync(order.Number);
        (untouched.Status, untouched.Payments.Count, untouched.FailedPaymentAttempts).Should().Be((OrderStatus.AwaitingPayment, 0, 0));
        (await host.GatewayChargesAsync(untouched.Id)).Should().BeEmpty();
        (await other.WalletBalanceAsync()).Should().Be(otherBalance);

        // The owner's payment waits for a one-time code: the other customer can neither read it nor complete it.
        var waiting = await owner.PayByCardAndWaitAsync(order.Number, "4242000000000034");
        waiting.Status.Should().Be(PaymentStatus.RequiresAction);
        (await other.Session.GetAsync($"/api/v1/orders/{order.Number}/payments/current")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.SubmitCodeAsync(order.Number, "123456")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var stillWaiting = await owner.PaymentAsync(order.Number);
        (stillWaiting.PaymentId, stillWaiting.Status, stillWaiting.NeedsOneTimeCode).Should().Be((waiting.PaymentId, PaymentStatus.RequiresAction, true));
        (await host.OrderAsync(order.Number)).Payments.Should().ContainSingle();
        (await other.WalletBalanceAsync()).Should().Be(otherBalance);
    }

    [Fact]
    [Trait("BR", "BR-PAY-03")]
    public async Task BR_PAY_03_the_one_time_code_approves_at_4_59()
    {
        var (shopper, order) = await OrderAsync();
        var payment = await shopper.PayByCardAndWaitAsync(order.Number, "4242000000000034");
        (payment.Status, payment.NeedsOneTimeCode, payment.TestOneTimeCode).Should().Be((PaymentStatus.RequiresAction, true, "123456"));
        payment.OneTimeCodeExpiresUtc.Should().Be(host.Clock.GetUtcNow().AddMinutes(5).UtcDateTime);
        payment.ServerTimeUtc.Should().Be(host.Clock.GetUtcNow().UtcDateTime);
        (await host.EventsAsync(payment.PaymentId)).Should().ContainSingle(e => e.Type == PaymentEventType.OneTimeCodeRequested)
            .Which.Detail.Should().Be("One-time code issued; valid for 5 minutes", "the event has its own time, shown in local time");

        host.Clock.Advance(TimeSpan.FromSeconds(4 * 60 + 59));
        var response = await shopper.SubmitCodeAsync(order.Number, "123456");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var approved = await response.ReadAsync<PaymentStatusDto>();
        (approved.Status, approved.OrderStatus).Should().Be((PaymentStatus.Succeeded, OrderStatus.Completed));
    }

    [Fact]
    [Trait("BR", "BR-PAY-03")]
    public async Task BR_PAY_03_the_right_code_at_5_01_is_too_late_and_the_attempt_expires()
    {
        var (shopper, order) = await OrderAsync();
        await shopper.PayByCardAndWaitAsync(order.Number, "4242000000000034");

        host.Clock.Advance(TimeSpan.FromSeconds(5 * 60 + 1));
        var response = await shopper.SubmitCodeAsync(order.Number, "123456");

        var expired = await response.ReadAsync<PaymentStatusDto>();
        (expired.Status, expired.DeclineMessage, expired.NeedsOneTimeCode).Should().Be((PaymentStatus.Expired, "The code has expired", false));
        (expired.OrderStatus, expired.AttemptsLeft, expired.RetryMode).Should().Be((OrderStatus.AwaitingPayment, 4, PaymentRetryMode.AnyMethod));
    }

    [Fact]
    [Trait("BR", "BR-ORD-02")]
    [Trait("BR", "BR-PAY-03")]
    public async Task BR_ORD_02_an_order_waiting_for_its_one_time_code_says_so_in_the_list_and_on_its_page()
    {
        var (shopper, order) = await OrderAsync();
        await shopper.PayByCardAndWaitAsync(order.Number, "4242000000000034");

        var history = await (await shopper.Session.GetAsync("/api/v1/orders")).ReadAsync<PagedResult<OrderSummaryDto>>();
        var detail = await (await shopper.Session.GetAsync($"/api/v1/orders/{order.Number}")).ReadAsync<OrderDetailDto>();

        var listed = history.Items.Single(o => o.Number == order.Number);
        (listed.StatusText, listed.AwaitingOneTimeCode, listed.CanPay).Should().Be(("Waiting for your one-time code", true, false));
        (detail.Status, detail.StatusText, detail.AwaitingOneTimeCode).Should().Be((OrderStatus.PaymentProcessing, "Waiting for your one-time code", true));
        detail.Payment!.Status.Should().Be(PaymentStatus.RequiresAction);

        // Once the code has run out, nothing waits for the customer any more.
        host.Clock.Advance(TimeSpan.FromSeconds(5 * 60 + 1));
        var afterExpiry = await (await shopper.Session.GetAsync("/api/v1/orders")).ReadAsync<PagedResult<OrderSummaryDto>>();
        var expired = afterExpiry.Items.Single(o => o.Number == order.Number);
        expired.StatusText.Should().NotBe("Waiting for your one-time code");
        expired.AwaitingOneTimeCode.Should().BeFalse();
        (await (await shopper.Session.GetAsync($"/api/v1/orders/{order.Number}")).ReadAsync<OrderDetailDto>()).AwaitingOneTimeCode
            .Should().BeFalse("an expired code is not waited for");
    }

    [Fact]
    [Trait("BR", "BR-ORD-02")]
    [Trait("BR", "BR-PAY-03")]
    public async Task BR_PAY_03_the_order_list_offers_payment_exactly_when_the_order_page_does()
    {
        var (shopper, order) = await OrderAsync();
        async Task<(bool Listed, bool Detail)> CanPayAsync()
        {
            var history = await (await shopper.Session.GetAsync("/api/v1/orders")).ReadAsync<PagedResult<OrderSummaryDto>>();
            var detail = await (await shopper.Session.GetAsync($"/api/v1/orders/{order.Number}")).ReadAsync<OrderDetailDto>();
            return (history.Items.Single(o => o.Number == order.Number).CanPay, detail.CanPay);
        }

        (await CanPayAsync()).Should().Be((true, true), "placed, not paid yet, within its hold");

        await shopper.PayByCardAndWaitAsync(order.Number, "4242000000000018");
        (await CanPayAsync()).Should().Be((true, true), "a declined attempt leaves 4 more");

        host.Clock.Advance(TimeSpan.FromMinutes(15));
        (await CanPayAsync()).Should().Be((false, false), "the hold is over");
    }

    [Fact]
    [Trait("BR", "BR-ORD-02")]
    [Trait("BR", "BR-PAY-03")]
    [Trait("BR", "BR-PAY-05")]
    public async Task BR_PAY_05_the_list_and_the_order_page_agree_on_a_failed_payment_with_and_without_attempts_left()
    {
        var (shopper, order) = await OrderAsync();
        var failed = await FailedCardPaymentAsync(order, MockGatewayChargeStatus.Error);
        host.Gateway.StatusUnavailable.Add(failed.Id);
        async Task<(OrderSummaryDto Listed, OrderDetailDto Detail)> ReadAsync()
        {
            var history = await (await shopper.Session.GetAsync("/api/v1/orders")).ReadAsync<PagedResult<OrderSummaryDto>>();
            var detail = await (await shopper.Session.GetAsync($"/api/v1/orders/{order.Number}")).ReadAsync<OrderDetailDto>();
            return (history.Items.Single(o => o.Number == order.Number), detail);
        }

        // Attempts left: the same card may try again, and the job may settle the payment meanwhile.
        var (listed, detail) = await ReadAsync();
        (listed.CanPay, detail.CanPay).Should().Be((true, true));
        (listed.StatusText, detail.StatusText).Should().Be(("Confirming your payment", "Confirming your payment"));
        detail.IsUpdating.Should().BeTrue("the gateway's answer changes the order on its own");

        // Attempts used up: nothing to start, but the order still waits for the gateway, and the pages keep reading it.
        await host.WithDbAsync(db => db.Orders.Where(o => o.Number == order.Number)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.FailedPaymentAttempts, 5), Ct));
        (listed, detail) = await ReadAsync();
        (listed.CanPay, detail.CanPay).Should().Be((false, false));
        (listed.StatusText, detail.StatusText).Should().Be(("Confirming your payment", "Confirming your payment"));
        (detail.Status, detail.IsUpdating).Should().Be((OrderStatus.AwaitingPayment, true));
    }

    [Fact]
    [Trait("BR", "BR-PAY-03")]
    public async Task BR_PAY_03_a_wrong_code_declines_and_trying_again_issues_a_new_code()
    {
        var (shopper, order) = await OrderAsync();
        var first = await shopper.PayByCardAndWaitAsync(order.Number, "4242000000000034");

        var wrong = await (await shopper.SubmitCodeAsync(order.Number, "654321")).ReadAsync<PaymentStatusDto>();
        (wrong.Status, wrong.DeclineMessage, wrong.RetryMode).Should().Be((PaymentStatus.Declined, "The code is not correct", PaymentRetryMode.AnyMethod));

        host.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await shopper.PayByCardAndWaitAsync(order.Number, "4242000000000034");
        second.PaymentId.Should().NotBe(first.PaymentId);
        (second.Status, second.NeedsOneTimeCode).Should().Be((PaymentStatus.RequiresAction, true));
        second.OneTimeCodeExpiresUtc.Should().Be(host.Clock.GetUtcNow().AddMinutes(5).UtcDateTime);
        (await (await shopper.SubmitCodeAsync(order.Number, "123456")).ReadAsync<PaymentStatusDto>()).Status.Should().Be(PaymentStatus.Succeeded);
    }

    [Fact]
    [Trait("BR", "BR-PAY-04")]
    [Trait("BR", "BR-PAY-01")]
    public async Task BR_PAY_04_a_slow_card_survives_a_refresh_refuses_a_second_payment_and_charges_once()
    {
        var (shopper, order) = await OrderAsync();
        var started = await AcceptedAsync(await shopper.PayByCardAsync(order.Number, "4242000000000042"));
        started.Status.Should().Be(PaymentStatus.Processing);
        await host.GatewayHasChargeAsync(started.PaymentId);

        // The page is refreshed after 5 s: it reads the payment from the server, still confirming.
        host.Clock.Advance(TimeSpan.FromSeconds(5));
        var refreshed = await shopper.PaymentAsync(order.Number);
        (refreshed.PaymentId, refreshed.Status, refreshed.OrderStatus, refreshed.RetryMode)
            .Should().Be((started.PaymentId, PaymentStatus.Processing, OrderStatus.PaymentProcessing, PaymentRetryMode.None));

        // A second submit, from this tab or another (wallet), is refused with the running payment.
        foreach (var second in new[] { await shopper.PayByCardAsync(order.Number, "4242000000000042"), await shopper.PayByWalletAsync(order.Number) })
        {
            second.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var problem = await second.ReadAsync<PaymentInProgressProblem>();
            (problem.Type, problem.Payment.PaymentId).Should().Be((PaymentProblemTypes.PaymentInProgress, started.PaymentId));
        }

        host.Clock.Advance(TimeSpan.FromSeconds(15));
        await host.ChargesFinishedAsync();

        (await shopper.PaymentAsync(order.Number)).Status.Should().Be(PaymentStatus.Succeeded);
        var charges = await host.GatewayChargesAsync((await host.OrderAsync(order.Number)).Id);
        charges.Should().ContainSingle().Which.Status.Should().Be(MockGatewayChargeStatus.Approved);
        (await host.OrderAsync(order.Number)).Payments.Should().ContainSingle();
    }

    [Fact]
    [Trait("BR", "BR-PAY-05")]
    public async Task BR_PAY_05_card_0059_errors_then_the_automatic_retry_with_the_same_payment_id_approves_once()
    {
        var (shopper, order) = await OrderAsync();

        var payment = await shopper.PayByCardAndWaitAsync(order.Number, "4242000000000059");

        payment.Status.Should().Be(PaymentStatus.Succeeded);
        var charge = (await host.GatewayChargesAsync((await host.OrderAsync(order.Number)).Id)).Should().ContainSingle().Subject;
        (charge.IdempotencyKey, charge.ErrorCount, charge.Status).Should().Be((payment.PaymentId, 1, MockGatewayChargeStatus.Approved));
        var events = await host.EventsAsync(payment.PaymentId);
        events.Select(e => e.Type).Where(t => t is PaymentEventType.GatewayError or PaymentEventType.Approved)
            .Should().Equal(PaymentEventType.GatewayError, PaymentEventType.Approved);
        events.Should().Contain(e => e.Type == PaymentEventType.SentToGateway && e.Detail == "Automatic retry, same payment id");
    }

    [Fact]
    [Trait("BR", "BR-PAY-01")]
    public async Task BR_PAY_01_an_approved_payment_id_sent_again_returns_the_first_result_without_a_second_charge()
    {
        var (shopper, order) = await OrderAsync();
        var payment = await shopper.PayByCardAndWaitAsync(order.Number, "4111111111111111");

        var gateway = host.App.Services.GetRequiredService<IPaymentGateway>();
        Lumen.Domain.Payments.CardNumber.TryParse("4242000000000018", out var otherCard).Should().BeTrue();
        var repeat = await gateway.ChargeAsync(new GatewayChargeRequest(payment.PaymentId, otherCard!, 999m), Ct);

        repeat.Status.Should().Be(GatewayChargeStatus.Approved);
        var charge = (await host.GatewayChargesAsync((await host.OrderAsync(order.Number)).Id)).Should().ContainSingle().Subject;
        (charge.Amount, charge.CardLast4).Should().Be((payment.Amount, "1111"));
    }

    [Theory]
    [Trait("BR", "BR-PAY-05")]
    [InlineData(MockGatewayChargeStatus.Error, PaymentStatus.Declined)]
    [InlineData(MockGatewayChargeStatus.Approved, PaymentStatus.Succeeded)]
    public async Task BR_PAY_05_a_failed_payment_is_settled_by_the_gateway_status_before_anything_new_is_charged(
        MockGatewayChargeStatus atGateway, PaymentStatus settledAs)
    {
        var (shopper, order) = await OrderAsync();
        var failed = await FailedCardPaymentAsync(order, atGateway);

        var payment = await AcceptedAsync(await shopper.PayByWalletAsync(order.Number));

        payment.Status.Should().Be(PaymentStatus.Succeeded);
        var saved = await host.OrderAsync(order.Number);
        saved.Status.Should().Be(OrderStatus.Completed);
        saved.Payments.Single(p => p.Id == failed.Id).Status.Should().Be(settledAs);
        if (settledAs == PaymentStatus.Succeeded)
        {
            // It had charged: that payment pays the order, nothing new is charged and the wallet is not touched.
            payment.PaymentId.Should().Be(failed.Id);
            saved.Payments.Should().ContainSingle();
            (await shopper.WalletBalanceAsync()).Should().Be(500.00m);
        }
        else
        {
            // Never charged: closed as declined (counted once, when it failed), and the wallet pays.
            saved.Payments.Single(p => p.Id == failed.Id).DeclineReason.Should().Be(DeclineReason.GatewayErrorUnresolvedNotCharged);
            saved.Payments.Single(p => p.Id != failed.Id).Method.Should().Be(PaymentMethod.Wallet);
            saved.FailedPaymentAttempts.Should().Be(1);
            (await shopper.WalletBalanceAsync()).Should().Be(500.00m - saved.Total);
        }
    }

    [Fact]
    [Trait("BR", "BR-PAY-05")]
    public async Task BR_PAY_05_while_the_gateway_cannot_tell_only_the_same_card_retries_the_same_payment_id()
    {
        var (shopper, order) = await OrderAsync();
        var failed = await FailedCardPaymentAsync(order, MockGatewayChargeStatus.Error);
        host.Gateway.StatusUnavailable.Add(failed.Id);

        (await shopper.PaymentAsync(order.Number)).Should().BeEquivalentTo(new
        {
            Status = PaymentStatus.Failed, RetryMode = PaymentRetryMode.SamePaymentOnly, CanRetry = true,
            DeclineMessage = PaymentStatusReader.FailedMessage,
        });
        foreach (var refused in new[] { await shopper.PayByWalletAsync(order.Number), await shopper.PayByCardAsync(order.Number, "4111111111111111") })
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await refused.ReadJsonAsync()).GetProperty("type").GetString().Should().Be(PaymentProblemTypes.SamePaymentOnly);
        }

        var retried = await shopper.PayByCardAndWaitAsync(order.Number, "4242 0000 0000 0059");

        (retried.PaymentId, retried.Status).Should().Be((failed.Id, PaymentStatus.Succeeded));
        (await host.GatewayChargesAsync((await host.OrderAsync(order.Number)).Id)).Should().ContainSingle("the same payment id was charged once");
        (await host.EventsAsync(failed.Id)).Should().Contain(e => e.Detail == "Try again by the customer, same payment id");
    }

    [Fact]
    [Trait("BR", "BR-PAY-05")]
    public async Task BR_PAY_05_a_failed_payment_settled_as_never_charged_just_before_the_claim_lets_a_new_payment_start()
    {
        var (shopper, order) = await OrderAsync();
        var failed = await FailedCardPaymentAsync(order, MockGatewayChargeStatus.Error);
        host.Gateway.StatusUnavailable.Add(failed.Id);

        // The request asks the gateway (no answer: still Failed, same card, so it may try again); then, before its claim,
        // the gateway answers the job, which settles the payment as never charged.
        host.Claims.BeforeNextClaim(async () =>
        {
            host.Gateway.StatusUnavailable.Clear();
            await using var scope = host.App.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PaymentHousekeeping>().SettleIfDueAsync(failed.Id, Ct);
        });

        var started = await AcceptedAsync(await shopper.PayByCardAsync(order.Number, "4242 0000 0000 0059"));
        await host.ChargesFinishedAsync();

        started.PaymentId.Should().NotBe(failed.Id, "the settled payment is final, so any method may pay with a new payment");
        var saved = await host.OrderAsync(order.Number);
        saved.Payments.Single(p => p.Id == failed.Id).Should().BeEquivalentTo(new
        {
            Status = PaymentStatus.Declined, DeclineReason = (DeclineReason?)DeclineReason.GatewayErrorUnresolvedNotCharged,
        });
        saved.Payments.Single(p => p.Id == started.PaymentId).Status.Should().Be(PaymentStatus.Succeeded);
        (saved.Status, saved.FailedPaymentAttempts).Should().Be((OrderStatus.Completed, 1));
        host.Gateway.ChargeCalls(failed.Id).Should().Be(0, "the settled payment id is never charged again");
    }

    [Fact]
    [Trait("BR", "BR-STOCK-01")]
    [Trait("BR", "BR-PAY-05")]
    public async Task BR_STOCK_01_a_hold_that_ends_during_the_gateway_status_call_stops_the_payment_and_charges_nothing()
    {
        var (shopper, order) = await OrderAsync();
        var failed = await FailedCardPaymentAsync(order, MockGatewayChargeStatus.Error);
        host.Gateway.StatusUnavailable.Add(failed.Id);
        host.Clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));

        // The status lookup Pay makes first takes 2 s: the hold ends while it runs.
        host.Gateway.DuringNextStatusLookup(failed.Id, () => host.Clock.Advance(TimeSpan.FromSeconds(2)));
        var response = await shopper.PayByCardAsync(order.Number, "4242 0000 0000 0059");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("type").GetString().Should().Be(PaymentProblemTypes.OrderNotPayable);
        await host.ChargesFinishedAsync();
        host.Gateway.ChargeCalls(failed.Id).Should().Be(0, "nothing is charged after the hold");
        var saved = await host.OrderAsync(order.Number);
        (saved.Status, saved.Payments.Single().Status).Should().Be((OrderStatus.AwaitingPayment, PaymentStatus.Failed));
    }

    /// <summary>
    /// A card 0059 payment whose automatic retry errored too (Failed, one attempt counted), and the gateway's record of it.
    /// The mock approves 0059 on its retry, so this state is written directly, as a crash or a gateway outage leaves it.
    /// </summary>
    private async Task<Payment> FailedCardPaymentAsync(OrderDetailDto order, MockGatewayChargeStatus atGateway)
    {
        var saved = await host.OrderAsync(order.Number);
        var now = host.Clock.GetUtcNow();
        var payment = new Payment
        {
            OrderId = saved.Id, Method = PaymentMethod.Card, Status = PaymentStatus.Failed, StatusChangedUtc = now, AutomaticRetryUsed = true,
            Amount = saved.Total, CardBrand = CardBrand.Visa, CardLast4 = "0059", CreatedUtc = now, CompletedUtc = now,
        };
        await host.WithDbAsync(async db =>
        {
            db.Payments.Add(payment);
            db.MockGatewayCharges.Add(new MockGatewayCharge
            {
                IdempotencyKey = payment.Id, CardBrand = CardBrand.Visa, CardLast4 = "0059", Behaviour = CardBehaviour.ErrorThenApprove,
                Status = atGateway, Amount = saved.Total, ErrorCount = 2, CreatedUtc = now,
            });
            await db.Orders.Where(o => o.Id == saved.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.FailedPaymentAttempts, 1), Ct);
            return await db.SaveChangesAsync(Ct);
        });
        return payment;
    }
}
