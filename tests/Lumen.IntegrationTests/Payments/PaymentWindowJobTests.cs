using System.Net;
using Lumen.Api.Orders;
using Lumen.Api.Partners;
using Lumen.Api.Payments;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Lumen.IntegrationTests.Catalog;
using Lumen.IntegrationTests.Checkout;
using Lumen.IntegrationTests.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumen.IntegrationTests.Payments;

/// <summary>
/// The payment and refund jobs scan every order, payment and refund in the shared database, and these tests move a
/// fake clock far ahead, so they run on their own, after the parallel tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PaymentJobsCollection
{
    public const string Name = "Payment jobs (scan the whole database)";
}

/// <summary>The hold-expiry and payment housekeeping job (BR-STOCK-01, BR-PAY-03, BR-PAY-05) on the real database.</summary>
[Collection(PaymentJobsCollection.Name)]
public sealed class PaymentWindowJobTests(CheckoutHost host) : IClassFixture<CheckoutHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<PaymentWindowReport> RunJobAsync() => host.App.Services.GetRequiredService<PaymentWindowJob>().RunOnceAsync(Ct);

    private async Task<(Shopper Shopper, OrderDetailDto Order, Guid ProductId)> OrderAsync(int quantity = 2, int onHand = 5)
    {
        var shopper = await host.NewShopperAsync();
        var mug = await host.OwnProductAsync(18.50m, onHand);
        await shopper.AddToCartAsync(mug, quantity);
        return (shopper, await shopper.PlaceOrderAsync(), mug.Id);
    }

    [Fact]
    [Trait("BR", "BR-STOCK-01")]
    public async Task BR_STOCK_01_an_unpaid_order_expires_when_its_15_minute_hold_ends_and_its_stock_is_available_again()
    {
        var (shopper, order, mugId) = await OrderAsync(quantity: 2);
        (await host.ProductAsync(mugId)).Reserved.Should().Be(2);

        host.Clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        await RunJobAsync();
        (await host.OrderAsync(order.Number)).Status.Should().Be(OrderStatus.AwaitingPayment);

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        (await RunJobAsync()).OrdersExpired.Should().BeGreaterThanOrEqualTo(1);

        (await host.OrderAsync(order.Number)).Status.Should().Be(OrderStatus.Expired);
        (await host.ProductAsync(mugId)).Reserved.Should().Be(0);
        var pay = await shopper.PayByCardAsync(order.Number, "4111111111111111");
        pay.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await pay.ReadJsonAsync()).GetProperty("type").GetString().Should().Be(PaymentProblemTypes.OrderNotPayable);
    }

    [Fact]
    [Trait("BR", "BR-STOCK-01")]
    [Trait("BR", "BR-PAY-04")]
    public async Task BR_STOCK_01_a_slow_card_still_running_when_the_hold_ends_keeps_the_order_and_it_becomes_paid()
    {
        var (shopper, order, mugId) = await OrderAsync(quantity: 1);
        host.Clock.Advance(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(50));
        var started = await (await shopper.PayByCardAsync(order.Number, "4242000000000042")).ReadAsync<PaymentStatusDto>();
        await host.GatewayHasChargeAsync(started.PaymentId);

        host.Clock.Advance(TimeSpan.FromSeconds(15)); // the hold ended 5 s ago; the gateway answers in 5 s
        await RunJobAsync();
        (await host.OrderAsync(order.Number)).Status.Should().Be(OrderStatus.PaymentProcessing);
        (await host.ProductAsync(mugId)).Reserved.Should().Be(1);

        host.Clock.Advance(TimeSpan.FromSeconds(5));
        await host.ChargesFinishedAsync();
        await RunJobAsync();

        (await shopper.PaymentAsync(order.Number)).Should().BeEquivalentTo(new { Status = PaymentStatus.Succeeded, OrderStatus = OrderStatus.Completed });
        var stock = await host.ProductAsync(mugId);
        (stock.OnHand, stock.Reserved).Should().Be((4, 0));
    }

    [Fact]
    [Trait("BR", "BR-PAY-03")]
    public async Task BR_PAY_03_an_abandoned_one_time_code_is_closed_by_the_job_and_a_late_code_cannot_approve_it()
    {
        var (shopper, order, _) = await OrderAsync();
        var payment = await shopper.PayByCardAndWaitAsync(order.Number, "4242000000000034");
        payment.Status.Should().Be(PaymentStatus.RequiresAction);

        host.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await RunJobAsync();

        var saved = await host.OrderAsync(order.Number);
        (saved.Status, saved.FailedPaymentAttempts).Should().Be((OrderStatus.AwaitingPayment, 1));
        saved.Payments.Single().Should().BeEquivalentTo(new { Status = PaymentStatus.Expired, DeclineReason = (DeclineReason?)DeclineReason.OneTimeCodeExpired });
        (await host.GatewayChargesAsync(saved.Id)).Single().Status.Should().Be(MockGatewayChargeStatus.Declined);

        var late = await (await shopper.SubmitCodeAsync(order.Number, "123456")).ReadAsync<PaymentStatusDto>();
        (late.Status, late.RetryMode).Should().Be((PaymentStatus.Expired, PaymentRetryMode.AnyMethod));
    }

    [Fact]
    [Trait("BR", "BR-PAY-03")]
    [Trait("BR", "BR-STOCK-01")]
    public async Task BR_PAY_03_the_fifth_failed_attempt_expires_the_order_and_releases_its_stock()
    {
        var (shopper, order, mugId) = await OrderAsync(quantity: 3);

        PaymentStatusDto last = null!;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            last = await shopper.PayByCardAndWaitAsync(order.Number, "4242000000000018");
            last.AttemptsLeft.Should().Be(5 - attempt);
        }

        (last.Status, last.OrderStatus, last.RetryMode, last.CanRetry).Should().Be((PaymentStatus.Declined, OrderStatus.Expired, PaymentRetryMode.None, false));
        (await host.ProductAsync(mugId)).Reserved.Should().Be(0);
        (await shopper.PayByWalletAsync(order.Number)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [Trait("BR", "BR-PAY-05")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BR_PAY_05_a_charge_whose_worker_died_is_reconciled_through_the_gateway_status(bool gatewayApprovedIt)
    {
        var (shopper, order, _) = await OrderAsync();
        var payment = await StuckPaymentAsync(order, PaymentStatus.Processing, gatewayApprovedIt ? MockGatewayChargeStatus.Approved : null);

        host.Clock.Advance(TimeSpan.FromSeconds(20) + TimeSpan.FromMinutes(1) - TimeSpan.FromSeconds(1));
        await RunJobAsync();
        (await host.OrderAsync(order.Number)).Payments.Single().Status.Should().Be(PaymentStatus.Processing, "it may still be running");

        host.Clock.Advance(TimeSpan.FromSeconds(2));
        await RunJobAsync();
        await RunJobAsync();

        var saved = await host.OrderAsync(order.Number);
        if (gatewayApprovedIt)
        {
            saved.Status.Should().Be(OrderStatus.Completed);
            saved.Payments.Single().Status.Should().Be(PaymentStatus.Succeeded);
        }
        else
        {
            // Never reached the gateway: Failed, then settled as never charged; the customer can pay again.
            (saved.Status, saved.FailedPaymentAttempts).Should().Be((OrderStatus.AwaitingPayment, 1));
            saved.Payments.Single().Should().BeEquivalentTo(new
            {
                Status = PaymentStatus.Declined, DeclineReason = (DeclineReason?)DeclineReason.GatewayErrorUnresolvedNotCharged,
            });
            (await shopper.PaymentAsync(order.Number)).RetryMode.Should().Be(PaymentRetryMode.AnyMethod);
        }

        (await host.EventsAsync(payment.Id)).Should().NotBeEmpty();
    }

    [Fact]
    [Trait("BR", "BR-PAY-05")]
    public async Task BR_PAY_05_an_error_whose_automatic_retry_never_ran_is_closed_as_failed_after_the_retry_window()
    {
        var (_, order, _) = await OrderAsync();
        var payment = await StuckPaymentAsync(order, PaymentStatus.Error, MockGatewayChargeStatus.Error);

        host.Clock.Advance(TimeSpan.FromSeconds(29));
        await RunJobAsync();
        (await host.OrderAsync(order.Number)).Payments.Single().Status.Should().Be(PaymentStatus.Error, "the retry may still come");

        host.Clock.Advance(TimeSpan.FromSeconds(2));
        await RunJobAsync();
        var failed = await host.OrderAsync(order.Number);
        (failed.Status, failed.FailedPaymentAttempts, failed.Payments.Single().Status).Should().Be((OrderStatus.AwaitingPayment, 1, PaymentStatus.Failed));

        await RunJobAsync();
        (await host.OrderAsync(order.Number)).Payments.Single().Status.Should().Be(PaymentStatus.Declined, "the gateway says it never charged");
        (await host.EventsAsync(payment.Id)).Select(e => e.Type).Should().Equal(PaymentEventType.Failed, PaymentEventType.Declined);
    }

    [Fact]
    [Trait("BR", "BR-PAY-05")]
    [Trait("BR", "BR-STOCK-01")]
    public async Task BR_PAY_05_an_order_is_not_expired_while_its_failed_payment_may_have_charged()
    {
        var (_, order, mugId) = await OrderAsync(quantity: 1);
        var payment = await StuckPaymentAsync(order, PaymentStatus.Failed, MockGatewayChargeStatus.Approved);
        host.Gateway.StatusUnavailable.Add(payment.Id);

        host.Clock.Advance(TimeSpan.FromMinutes(16));
        await RunJobAsync();
        (await host.OrderAsync(order.Number)).Status.Should().Be(OrderStatus.AwaitingPayment, "the gateway could not say whether it charged");
        (await host.ProductAsync(mugId)).Reserved.Should().Be(1);

        host.Gateway.StatusUnavailable.Clear();
        await RunJobAsync();

        var saved = await host.OrderAsync(order.Number);
        (saved.Status, saved.Payments.Single().Status).Should().Be((OrderStatus.Completed, PaymentStatus.Succeeded), "it had charged, so it pays the order");
        (await host.ProductAsync(mugId)).Reserved.Should().Be(0);
    }

    [Fact]
    [Trait("BR", "BR-PAY-03")]
    [Trait("BR", "BR-PAY-05")]
    [Trait("BR", "BR-STOCK-01")]
    public async Task BR_PAY_05_a_fifth_attempt_that_fails_holds_the_order_until_the_gateway_says_it_never_charged()
    {
        // Each step in its own scope, as the payment window job runs it. Called directly, not through RunJobAsync, so
        // payments other tests leave due cannot push this one out of a run's batch.
        async Task SettleAsync(Guid paymentId)
        {
            await using var scope = host.App.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PaymentHousekeeping>().SettleIfDueAsync(paymentId, Ct);
        }

        async Task<bool> ExpireAsync(Guid orderId)
        {
            await using var scope = host.App.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<PaymentHousekeeping>().ExpireIfDueAsync(orderId, Ct);
        }

        var (shopper, order, mugId) = await OrderAsync(quantity: 1);
        var payment = await StuckPaymentAsync(order, PaymentStatus.Error, MockGatewayChargeStatus.Error);
        await host.WithDbAsync(db => db.Orders.Where(o => o.Number == order.Number)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.FailedPaymentAttempts, 4), Ct));
        host.Gateway.StatusUnavailable.Add(payment.Id);

        // Its automatic retry never ran: the fifth attempt closes as Failed, which may have charged, so nothing expires.
        host.Clock.Advance(TimeSpan.FromSeconds(31));
        await SettleAsync(payment.Id);
        var held = await host.OrderAsync(order.Number);
        (held.Status, held.FailedPaymentAttempts, held.Payments.Single().Status).Should().Be((OrderStatus.AwaitingPayment, 5, PaymentStatus.Failed));
        (await ExpireAsync(held.Id)).Should().BeFalse();
        (await host.ProductAsync(mugId)).Reserved.Should().Be(1);

        // Nothing left to try: the customer is told to wait for the card provider, not to try again.
        var shown = await shopper.PaymentAsync(order.Number);
        (shown.Status, shown.RetryMode, shown.DeclineMessage)
            .Should().Be((PaymentStatus.Failed, PaymentRetryMode.None, PaymentStatusReader.FailedAwaitingGatewayMessage));

        // The gateway answers that it never charged: Declined, then the order expires and its stock is released.
        host.Gateway.StatusUnavailable.Clear();
        await SettleAsync(payment.Id);
        (await ExpireAsync(held.Id)).Should().BeTrue();
        var expired = await host.OrderAsync(order.Number);
        (expired.Status, expired.FailedPaymentAttempts, expired.Payments.Single().Status)
            .Should().Be((OrderStatus.Expired, 5, PaymentStatus.Declined), "the failed attempt was counted once, when it failed");
        (await host.ProductAsync(mugId)).Reserved.Should().Be(0);
    }

    [Fact]
    [Trait("BR", "BR-STOCK-01")]
    [Trait("BR", "BR-PAY-05")]
    public async Task BR_STOCK_01_failed_payments_the_gateway_cannot_settle_never_stop_other_orders_expiring_or_payments_closing()
    {
        await RunJobAsync(); // whatever earlier tests left due is handled first, so only this test's rows are due below

        // A gateway status outage: more Failed payments than one batch, all older than anything else, that the gateway
        // cannot settle, so they stay due on every run (their orders are not expired: they may have charged).
        var batch = new PaymentJobsOptions().BatchSize;
        var shopper = await host.NewShopperAsync();
        var product = await host.OwnProductAsync();
        var longAgo = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var unsettled = new List<Guid>();
        Payment errored = null!;
        await host.WithDbAsync(async db =>
        {
            for (var i = 0; i < batch + 5; i++)
            {
                var order = OrderingTestData.NewOrder(shopper.Id, product.Id);
                (order.CreatedUtc, order.HoldExpiresUtc, order.FailedPaymentAttempts) = (longAgo, longAgo.AddMinutes(15), 1);
                var payment = OrderingTestData.NewPayment(order.Id, PaymentStatus.Failed);
                (payment.CreatedUtc, payment.StatusChangedUtc, payment.AutomaticRetryUsed) = (longAgo, longAgo.AddMinutes(1), true);
                db.AddRange(order, payment);
                host.Gateway.StatusUnavailable.Add(payment.Id);
                unsettled.Add(order.Id);
            }

            // Behind them in the sweep: a gateway error whose automatic retry never ran; closing it needs no gateway call.
            var claimed = OrderingTestData.NewOrder(shopper.Id, product.Id);
            (claimed.Status, claimed.CreatedUtc, claimed.HoldExpiresUtc) = (OrderStatus.PaymentProcessing, longAgo, longAgo.AddMinutes(15));
            errored = OrderingTestData.NewPayment(claimed.Id, PaymentStatus.Error);
            (errored.CreatedUtc, errored.StatusChangedUtc) = (longAgo, longAgo.AddMinutes(2));
            db.AddRange(claimed, errored);
            return await db.SaveChangesAsync(Ct);
        });

        // And an ordinary unpaid order whose 15-minute hold ends now.
        var (_, due, mugId) = await OrderAsync(quantity: 2);
        host.Clock.Advance(TimeSpan.FromMinutes(15));

        using var job = new PaymentWindowJob(
            host.App.Services.GetRequiredService<IServiceScopeFactory>(),
            host.App.Services.GetRequiredService<PaymentWindowPolicy>(),
            host.App.Services.GetRequiredService<IOptionsMonitor<PaymentOptions>>(),
            new FixedOptions<PaymentJobsOptions>(new PaymentJobsOptions()),
            host.App.Services.GetRequiredService<IOptionsMonitor<JobsOptions>>(),
            host.Clock,
            NullLogger<PaymentWindowJob>.Instance);
        await job.RunOnceAsync(Ct);

        (await host.OrderAsync(due.Number)).Status.Should().Be(OrderStatus.Expired);
        (await host.ProductAsync(mugId)).Reserved.Should().Be(0, "the expired order's stock is available again");
        (await host.WithDbAsync(db => db.Orders.CountAsync(o => unsettled.Contains(o.Id) && o.Status == OrderStatus.AwaitingPayment, Ct)))
            .Should().Be(unsettled.Count, "an order is not expired while its failed payment may have charged");

        // The first run's batch was all unsettleable payments; the next run continues after them instead of retrying them.
        await job.RunOnceAsync(Ct);
        (await host.WithDbAsync(db => db.Payments.AsNoTracking().SingleAsync(p => p.Id == errored.Id, Ct))).Status
            .Should().Be(PaymentStatus.Failed, "the error behind them is closed on the second run");
    }

    /// <summary>
    /// A card payment left as a crash or a gateway outage leaves it: the order claimed (or, for Failed, back to awaiting
    /// payment with the attempt counted), and the gateway's own record when it got the charge.
    /// </summary>
    private async Task<Payment> StuckPaymentAsync(OrderDetailDto order, PaymentStatus status, MockGatewayChargeStatus? atGateway)
    {
        var saved = await host.OrderAsync(order.Number);
        var now = host.Clock.GetUtcNow();
        var payment = new Payment
        {
            OrderId = saved.Id, Method = PaymentMethod.Card, Status = status, StatusChangedUtc = now, Amount = saved.Total,
            CardBrand = CardBrand.Visa, CardLast4 = "1111", CreatedUtc = now, AutomaticRetryUsed = status == PaymentStatus.Failed,
        };
        await host.WithDbAsync(async db =>
        {
            db.Payments.Add(payment);
            if (atGateway is { } gatewayStatus)
            {
                db.MockGatewayCharges.Add(new MockGatewayCharge
                {
                    IdempotencyKey = payment.Id, CardBrand = CardBrand.Visa, CardLast4 = "1111",
                    Behaviour = gatewayStatus == MockGatewayChargeStatus.Error ? CardBehaviour.ErrorThenApprove : CardBehaviour.Approve,
                    Status = gatewayStatus, Amount = saved.Total, CreatedUtc = now,
                });
            }

            var orderStatus = status == PaymentStatus.Failed ? OrderStatus.AwaitingPayment : OrderStatus.PaymentProcessing;
            await db.Orders.Where(o => o.Id == saved.Id).ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, orderStatus)
                .SetProperty(o => o.FailedPaymentAttempts, status == PaymentStatus.Failed ? 1 : 0), Ct);
            return await db.SaveChangesAsync(Ct);
        });
        return payment;
    }

    [Fact]
    [Trait("BR", "BR-PAY-05")]
    [Trait("BR", "BR-STOCK-01")]
    public async Task BR_PAY_05_a_failed_payment_the_gateway_cannot_settle_is_reported_while_it_holds_the_order()
    {
        var (_, order, _) = await OrderAsync(quantity: 1);
        var payment = await StuckPaymentAsync(order, PaymentStatus.Failed, MockGatewayChargeStatus.Approved);
        host.Gateway.StatusUnavailable.Add(payment.Id);
        var warnAfter = TimeSpan.FromMinutes(10);
        var logger = new WarningLogger<PaymentWindowJob>();
        using var job = new PaymentWindowJob(
            host.App.Services.GetRequiredService<IServiceScopeFactory>(),
            host.App.Services.GetRequiredService<PaymentWindowPolicy>(),
            host.App.Services.GetRequiredService<IOptionsMonitor<PaymentOptions>>(),
            new FixedOptions<PaymentJobsOptions>(new PaymentJobsOptions { UnsettledFailedWarnAfter = warnAfter }),
            host.App.Services.GetRequiredService<IOptionsMonitor<JobsOptions>>(),
            host.Clock,
            logger);

        host.Clock.Advance(warnAfter);
        (await job.RunOnceAsync(Ct)).UnsettledFailedPayments.Should().BeGreaterThanOrEqualTo(1);

        logger.Warnings.Should().ContainSingle().Which.Should().Contain("stay held");
        (await host.OrderAsync(order.Number)).Status.Should().Be(OrderStatus.AwaitingPayment, "the hold is over, but it may have charged");

        // Repeated at most once per warning period while it lasts, not on every run.
        await job.RunOnceAsync(Ct);
        logger.Warnings.Should().ContainSingle();
        host.Clock.Advance(warnAfter);
        await job.RunOnceAsync(Ct);
        logger.Warnings.Should().HaveCount(2);
    }

    /// <summary>Keeps the job's warnings, formatted, so a test can read what an operator would see.</summary>
    private sealed class WarningLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
