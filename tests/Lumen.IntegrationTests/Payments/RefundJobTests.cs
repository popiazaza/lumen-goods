using Lumen.Api.Orders;
using Lumen.Api.Payments;
using Lumen.Api.Wallets;
using Lumen.Domain.Catalog;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Lumen.IntegrationTests.Checkout;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lumen.IntegrationTests.Payments;

/// <summary>
/// Refund execution (BR-ORD-03, BR-PAY-06): refunds recorded Pending (as the partner order work records them when a
/// partner rejects a line) are paid out to the original payment method, once, however often the job runs or crashes.
/// The order is a Lumen mug and a Kestrel line of 2 × $16.22 (tax $2.60); Kestrel rejects its line, so $32.44 + $2.60
/// + $4.99 shipping = $40.03 is refunded.
/// </summary>
[Collection(PaymentJobsCollection.Name)]
public sealed class RefundJobTests(CheckoutHost host) : IClassFixture<CheckoutHost>
{
    private const decimal RefundAmount = 40.03m;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<RefundRunReport> RunJobAsync() => host.App.Services.GetRequiredService<RefundJob>().RunOnceAsync(Ct);

    private Task<Refund> RefundAsync(Guid id) => host.WithDbAsync(db => db.Refunds.AsNoTracking().SingleAsync(r => r.Id == id, Ct));

    private async Task<(Shopper Shopper, Order Order)> PaidOrderAsync(bool byWallet)
    {
        var shopper = await host.NewShopperAsync();
        await shopper.AddToCartAsync(await host.OwnProductAsync(18.50m), 1);
        await shopper.AddToCartAsync(await host.KestrelProductAsync(14.95m), 2);
        var order = await shopper.PlaceOrderAsync();
        if (byWallet)
        {
            (await shopper.PayByWalletAsync(order.Number)).EnsureSuccessStatusCode();
        }
        else
        {
            await shopper.PayByCardAndWaitAsync(order.Number, "4111111111111111");
        }

        return (shopper, await host.OrderAsync(order.Number));
    }

    /// <summary>Records the Kestrel line's refund the way the partner order work does, in one transaction.</summary>
    private async Task<Refund> RecordKestrelRefundAsync(Order order)
    {
        var line = order.Lines.Single(l => l.Source == ProductSources.Kestrel);
        var shipment = order.Shipments.Single(s => s.Source == ProductSources.Kestrel);
        var lineAmount = line.LineSubtotal + line.Tax;
        var refund = new Refund
        {
            OrderId = order.Id,
            PaymentId = order.Payments.Single(p => p.Status == PaymentStatus.Succeeded).Id,
            Amount = lineAmount + shipment.Fee,
            ShipmentId = shipment.Id,
            ShippingAmount = shipment.Fee,
            Reason = "Not supplied by Kestrel Outfitters (OUT_OF_STOCK)",
            CreatedUtc = host.Clock.GetUtcNow(),
            NextAttemptUtc = host.Clock.GetUtcNow(),
            Lines = [new RefundLine { OrderId = order.Id, OrderLineId = line.Id, Amount = lineAmount }],
        };
        refund.Amount.Should().Be(RefundAmount);

        await host.WithDbAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.Refunds.Add(refund);
            await db.SaveChangesAsync(Ct);
            await db.Orders.Where(o => o.Id == order.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.RefundedTotal, o => o.RefundedTotal + refund.Amount), Ct);
            await db.OrderLines.Where(l => l.Id == line.Id).ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, OrderLineStatus.Rejected).SetProperty(l => l.RefundedAmount, lineAmount), Ct);
            await db.OrderShipments.Where(s => s.Id == shipment.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.FeeRefunded, true), Ct);
            await transaction.CommitAsync(Ct);
            return 0;
        });
        return refund;
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_a_card_refund_is_paid_out_through_the_gateway_once()
    {
        var (_, order) = await PaidOrderAsync(byWallet: false);
        var refund = await RecordKestrelRefundAsync(order);

        await RunJobAsync();

        var done = await RefundAsync(refund.Id);
        (done.Status, done.Attempts, done.NextAttemptUtc, done.CompletedUtc).Should().Be((RefundStatus.Succeeded, 1, (DateTimeOffset?)null, host.Clock.GetUtcNow()));
        var charge = (await host.GatewayChargesAsync(order.Id)).Single();
        charge.RefundedAmount.Should().Be(RefundAmount);
        (await host.EventsAsync(refund.PaymentId)).Should().ContainSingle(e => e.Type == PaymentEventType.Refunded)
            .Which.Amount.Should().Be(RefundAmount);

        await RunJobAsync();
        (await host.GatewayChargesAsync(order.Id)).Single().RefundedAmount.Should().Be(RefundAmount, "a second run pays nothing more");
        (await host.OrderAsync(order.Number)).RefundedTotal.Should().Be(RefundAmount, "the order counted the refund when it was recorded");
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_a_refund_resent_after_a_crash_is_not_paid_out_twice()
    {
        var (_, order) = await PaidOrderAsync(byWallet: false);
        var refund = await RecordKestrelRefundAsync(order);

        // The job called the gateway, then the process died before recording the result.
        var gateway = host.App.Services.GetRequiredService<IPaymentGateway>();
        (await gateway.RefundAsync(new GatewayRefundRequest(refund.Id, refund.PaymentId, refund.Amount), Ct)).Succeeded.Should().BeTrue();

        await RunJobAsync();

        (await RefundAsync(refund.Id)).Status.Should().Be(RefundStatus.Succeeded);
        (await host.GatewayChargesAsync(order.Id)).Single().RefundedAmount.Should().Be(RefundAmount);
        (await host.WithDbAsync(db => db.MockGatewayRefunds.CountAsync(r => r.IdempotencyKey == refund.Id, Ct))).Should().Be(1);
    }

    [Fact]
    [Trait("BR", "BR-PAY-06")]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_PAY_06_a_wallet_refund_credits_the_wallet_once_with_a_log_entry_for_the_order()
    {
        var (shopper, order) = await PaidOrderAsync(byWallet: true);
        var paidBalance = await shopper.WalletBalanceAsync();
        var refund = await RecordKestrelRefundAsync(order);

        await RunJobAsync();
        await RunJobAsync();

        (await RefundAsync(refund.Id)).Status.Should().Be(RefundStatus.Succeeded);
        (await shopper.WalletBalanceAsync()).Should().Be(paidBalance + RefundAmount);
        var credits = await host.WithDbAsync(db => db.WalletTransactions.AsNoTracking()
            .Where(t => t.CustomerId == shopper.Id && t.Kind == WalletTransactionKind.Refund).ToListAsync(Ct));
        credits.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Amount = RefundAmount, BalanceAfter = paidBalance + RefundAmount, OrderId = (Guid?)order.Id, RefundId = (Guid?)refund.Id,
        });
    }

    [Fact]
    [Trait("BR", "BR-PAY-06")]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_PAY_06_a_wallet_refund_already_credited_but_not_recorded_is_marked_succeeded_without_a_second_credit()
    {
        var (shopper, order) = await PaidOrderAsync(byWallet: true);
        var paidBalance = await shopper.WalletBalanceAsync();
        var refund = await RecordKestrelRefundAsync(order);

        // An earlier run credited the wallet, and its success was lost (the refund row still says Pending).
        await host.WithDbAsync(async db =>
        {
            await db.Wallets.Where(w => w.CustomerId == shopper.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.Balance, w => w.Balance + RefundAmount), Ct);
            db.WalletTransactions.Add(new WalletTransaction
            {
                CustomerId = shopper.Id, Amount = RefundAmount, BalanceAfter = paidBalance + RefundAmount, Kind = WalletTransactionKind.Refund,
                OrderId = order.Id, RefundId = refund.Id, CreatedUtc = host.Clock.GetUtcNow(),
            });
            return await db.SaveChangesAsync(Ct);
        });

        await RunJobAsync();

        var done = await RefundAsync(refund.Id);
        (done.Status, done.NextAttemptUtc).Should().Be((RefundStatus.Succeeded, (DateTimeOffset?)null), "the credit exists, so the refund is final");
        (await shopper.WalletBalanceAsync()).Should().Be(paidBalance + RefundAmount, "the wallet is credited once");
        (await host.WithDbAsync(db => db.WalletTransactions.CountAsync(t => t.RefundId == refund.Id, Ct))).Should().Be(1);
        (await host.EventsAsync(refund.PaymentId)).Should().ContainSingle(e => e.Type == PaymentEventType.Refunded);
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_a_refused_card_refund_is_retried_later_with_the_same_refund_id()
    {
        var (_, order) = await PaidOrderAsync(byWallet: false);
        var refund = await RecordKestrelRefundAsync(order);
        var charge = (await host.GatewayChargesAsync(order.Id)).Single();

        // The gateway refuses it for now (as if the charge were already refunded elsewhere).
        await host.WithDbAsync(db => db.MockGatewayCharges.Where(c => c.IdempotencyKey == charge.IdempotencyKey)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.RefundedAmount, c => c.Amount), Ct));
        (await RunJobAsync()).Failed.Should().BeGreaterThanOrEqualTo(1);

        var failed = await RefundAsync(refund.Id);
        (failed.Status, failed.Attempts, failed.NextAttemptUtc).Should().Be((RefundStatus.Failed, 1, host.Clock.GetUtcNow().AddMinutes(1)));
        failed.LastError.Should().Contain("Refund refused");
        (await host.EventsAsync(refund.PaymentId)).Should().Contain(e => e.Type == PaymentEventType.RefundFailed);

        await host.WithDbAsync(db => db.MockGatewayCharges.Where(c => c.IdempotencyKey == charge.IdempotencyKey)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.RefundedAmount, 0m), Ct));
        await RunJobAsync();
        (await RefundAsync(refund.Id)).Status.Should().Be(RefundStatus.Failed, "not due yet");

        host.Clock.Advance(TimeSpan.FromMinutes(1));
        await RunJobAsync();

        var done = await RefundAsync(refund.Id);
        (done.Id, done.Status, done.Attempts).Should().Be((refund.Id, RefundStatus.Succeeded, 2));
        (await host.GatewayChargesAsync(order.Id)).Single().RefundedAmount.Should().Be(RefundAmount);
    }
}
