using Lumen.Api.Payments;
using Lumen.Domain.Payments;
using Lumen.IntegrationTests.Infrastructure;
using Lumen.IntegrationTests.Orders;
using Microsoft.EntityFrameworkCore;

namespace Lumen.IntegrationTests.Payments;

/// <summary>The payment tables on real PostgreSQL: one running payment per order, and no card data beyond the last 4 digits.</summary>
public sealed class PaymentPersistenceTests(LumenApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [Trait("BR", "BR-PAY-01")]
    [InlineData(PaymentStatus.Created)]
    [InlineData(PaymentStatus.Processing)]
    [InlineData(PaymentStatus.RequiresAction)]
    [InlineData(PaymentStatus.Error)]
    [InlineData(PaymentStatus.Failed)]
    public async Task BR_PAY_01_a_second_payment_cannot_start_while_one_is_not_final(PaymentStatus running)
    {
        var order = await factory.NewSavedOrderAsync();
        await factory.SaveAsync(OrderingTestData.NewPayment(order.Id, running));

        var wallet = OrderingTestData.NewPayment(order.Id, PaymentStatus.Created, PaymentMethod.Wallet);

        (await factory.SaveRejectedAsync(wallet)).ShouldBeUniqueViolation("IX_Payments_OrderId_NotFinal");
    }

    [Fact]
    [Trait("BR", "BR-PAY-02")]
    public async Task BR_PAY_02_after_final_outcomes_the_order_can_be_paid_again_by_another_method()
    {
        var order = await factory.NewSavedOrderAsync();
        await factory.SaveAsync(
            OrderingTestData.NewPayment(order.Id, PaymentStatus.Declined),
            OrderingTestData.NewPayment(order.Id, PaymentStatus.Expired));

        await factory.SaveAsync(OrderingTestData.NewPayment(order.Id, PaymentStatus.Processing, PaymentMethod.Wallet));

        await using var db = factory.NewContext();
        (await db.Payments.CountAsync(p => p.OrderId == order.Id, Ct)).Should().Be(3);
    }

    [Theory]
    [Trait("BR", "BR-PAY-02")]
    [InlineData("12a4")]
    [InlineData("123")]
    public async Task BR_PAY_02_only_four_digits_of_a_card_can_be_stored(string last4)
    {
        var order = await factory.NewSavedOrderAsync();
        var payment = OrderingTestData.NewPayment(order.Id, PaymentStatus.Declined);
        payment.CardLast4 = last4;

        (await factory.SaveRejectedAsync(payment)).ShouldBeCheckViolation("CK_Payments_CardLast4IsFourDigits");
    }

    [Fact]
    [Trait("BR", "BR-PAY-02")]
    public async Task BR_PAY_02_a_full_card_number_does_not_fit_the_column()
    {
        var order = await factory.NewSavedOrderAsync();
        var payment = OrderingTestData.NewPayment(order.Id, PaymentStatus.Declined);
        payment.CardLast4 = "4111111111111111";

        (await factory.SaveRejectedAsync(payment)).SqlState.Should().Be(Npgsql.PostgresErrorCodes.StringDataRightTruncation);
    }

    [Fact]
    [Trait("BR", "BR-PAY-02")]
    public async Task BR_PAY_02_wallet_payments_carry_no_card_data_and_card_payments_need_it()
    {
        var order = await factory.NewSavedOrderAsync();

        var walletWithCard = OrderingTestData.NewPayment(order.Id, PaymentStatus.Declined, PaymentMethod.Wallet);
        walletWithCard.CardBrand = CardBrand.Visa;
        walletWithCard.CardLast4 = "1111";
        (await factory.SaveRejectedAsync(walletWithCard)).ShouldBeCheckViolation("CK_Payments_CardDataOnlyForCards");

        var cardWithout = OrderingTestData.NewPayment(order.Id, PaymentStatus.Declined);
        cardWithout.CardLast4 = null;
        (await factory.SaveRejectedAsync(cardWithout)).ShouldBeCheckViolation("CK_Payments_CardDataOnlyForCards");
    }

    [Fact]
    public async Task The_timeline_keeps_events_in_order_and_the_payment_snapshot_feeds_the_window_rules()
    {
        var order = await factory.NewSavedOrderAsync();
        var payment = OrderingTestData.NewPayment(order.Id, PaymentStatus.Error);
        payment.AutomaticRetryUsed = true;
        PaymentEvent Event(PaymentEventType type, int second) => new()
        {
            PaymentId = payment.Id, OrderId = order.Id, Type = type, OccurredUtc = OrderingTestData.Now.AddSeconds(second),
            Amount = type == PaymentEventType.Approved ? 46.95m : null, Detail = type == PaymentEventType.GatewayError ? "Gateway error" : null,
        };
        await factory.SaveAsync(payment, Event(PaymentEventType.Approved, 2), Event(PaymentEventType.GatewayError, 1));

        await using var db = factory.NewContext();
        var timeline = await db.PaymentEvents.Where(e => e.OrderId == order.Id).OrderBy(e => e.OccurredUtc).Select(e => e.Type).ToListAsync(Ct);
        timeline.Should().Equal(PaymentEventType.GatewayError, PaymentEventType.Approved);

        var loaded = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id, Ct);
        loaded.Snapshot.Should().Be(new PaymentSnapshot(PaymentStatus.Error, OrderingTestData.Now, AutomaticRetryUsed: true));
    }

    [Fact]
    public async Task A_payment_event_belongs_to_a_payment_of_the_same_order()
    {
        var order = await factory.NewSavedOrderAsync();
        var other = await factory.NewSavedOrderAsync();
        var payment = OrderingTestData.NewPayment(order.Id, PaymentStatus.Declined);
        await factory.SaveAsync(payment);

        var misfiled = new PaymentEvent
        {
            PaymentId = payment.Id, OrderId = other.Id, Type = PaymentEventType.Declined, OccurredUtc = OrderingTestData.Now,
        };

        (await factory.SaveRejectedAsync(misfiled)).ShouldBeForeignKeyViolation("FK_PaymentEvents_Payments_PaymentId_OrderId");
    }

    [Fact]
    [Trait("BR", "BR-PAY-05")]
    public async Task BR_PAY_05_the_housekeeping_job_can_find_payments_stuck_in_a_status()
    {
        await using var db = factory.NewContext();
        var indexes = await db.Database
            .SqlQuery<string>($"SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename = 'Payments'")
            .ToListAsync(Ct);

        indexes.Should().Contain(["IX_Payments_Status_StatusChangedUtc", "IX_Payments_OrderId_NotFinal"]);
    }

    [Fact]
    [Trait("BR", "BR-PAY-05")]
    public async Task BR_PAY_05_a_failed_payment_settled_as_not_charged_lets_a_new_payment_start()
    {
        var order = await factory.NewSavedOrderAsync();
        var failed = OrderingTestData.NewPayment(order.Id, PaymentStatus.Failed);
        await factory.SaveAsync(failed);
        (await factory.SaveRejectedAsync(OrderingTestData.NewPayment(order.Id, PaymentStatus.Created, PaymentMethod.Wallet)))
            .ShouldBeUniqueViolation("IX_Payments_OrderId_NotFinal");

        await using (var db = factory.NewContext())
        {
            var settled = await db.Payments.SingleAsync(p => p.Id == failed.Id, Ct);
            settled.Status = PaymentTransitions.Payment.Move(settled.Status, PaymentStatus.Declined);
            settled.DeclineReason = DeclineReason.GatewayErrorUnresolvedNotCharged;
            await db.SaveChangesAsync(Ct);
        }

        await factory.SaveAsync(OrderingTestData.NewPayment(order.Id, PaymentStatus.Created, PaymentMethod.Wallet));
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_the_mock_gateway_never_refunds_more_than_it_charged_nor_the_same_refund_twice()
    {
        var charge = new MockGatewayCharge
        {
            IdempotencyKey = Guid.NewGuid(), CardBrand = CardBrand.Visa, CardLast4 = "0059", Behaviour = CardBehaviour.ErrorThenApprove,
            Status = MockGatewayChargeStatus.Approved, Amount = 46.95m, ErrorCount = 1, CreatedUtc = OrderingTestData.Now,
            RefundedAmount = 46.96m,
        };
        (await factory.SaveRejectedAsync(charge)).ShouldBeCheckViolation("CK_MockGatewayCharges_RefundedWithinAmount");

        charge.RefundedAmount = 46.95m;
        var refund = new MockGatewayRefund
        {
            IdempotencyKey = Guid.NewGuid(), ChargeIdempotencyKey = charge.IdempotencyKey, Amount = 46.95m, CreatedUtc = OrderingTestData.Now,
        };
        await factory.SaveAsync(charge, refund);

        var sameRefundAgain = new MockGatewayRefund
        {
            IdempotencyKey = refund.IdempotencyKey, ChargeIdempotencyKey = charge.IdempotencyKey, Amount = 46.95m, CreatedUtc = OrderingTestData.Now,
        };
        (await factory.SaveRejectedAsync(sameRefundAgain)).ShouldBeUniqueViolation("PK_MockGatewayRefunds");
    }
}
