using System.Net;
using Lumen.Api.Orders;
using Lumen.Api.Payments;
using Lumen.Api.Wallets;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Lumen.IntegrationTests.Catalog;
using Lumen.IntegrationTests.Checkout;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Lumen.IntegrationTests.Payments;

/// <summary>
/// BR-PAY-06 with many callers: one customer pays several orders from a wallet that covers only one of them, all at the
/// same moment, on the real API and database. The conditional debit lets exactly one through.
/// </summary>
[Collection(ManyCallersCollection.Name)]
public sealed class WalletRaceTests(CheckoutHost host) : IClassFixture<CheckoutHost>
{
    /// <summary>One own product at $87.97: with 8 % tax ($7.04) and Standard shipping ($4.99) the order is exactly $100.00.</summary>
    private const decimal HundredDollarPrice = 87.97m;

    /// <summary>The cap on unpaid orders per customer is 3, so this is the most one customer can have waiting.</summary>
    private const int UnpaidOrders = 3;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("BR", "BR-PAY-06")]
    public async Task BR_PAY_06_three_orders_paid_from_a_wallet_covering_one_at_the_same_moment_debit_it_once_and_never_below_zero()
    {
        var shopper = await host.NewShopperAsync();
        await shopper.AddToCartAsync(await host.OwnProductAsync(HundredDollarPrice, onHand: UnpaidOrders), 1);
        var orders = new List<OrderDetailDto>();
        for (var i = 0; i < UnpaidOrders; i++)
        {
            orders.Add(await shopper.PlaceOrderAsync()); // the cart is unchanged until an order is paid
        }

        orders.Should().OnlyContain(o => o.Totals.Total == 100.00m);
        await shopper.SetWalletBalanceAsync(100.00m);

        var race = ManyCallers.AllAtOnceAsync(orders.Select(o => (Func<Task<HttpResponseMessage>>)(() => shopper.PayByWalletAsync(o.Number))));
        await ManyCallers.CheckWhileRunningAsync(race, async () =>
            (await shopper.WalletBalanceAsync()).Should().BeGreaterThanOrEqualTo(0m, "the balance never goes negative, even mid-race"));
        var responses = await race;

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Accepted);
        var payments = await Task.WhenAll(responses.Select(r => r.ReadAsync<PaymentStatusDto>()));
        payments.Should().ContainSingle(p => p.Status == PaymentStatus.Succeeded);
        payments.Where(p => p.Status != PaymentStatus.Succeeded).Should().HaveCount(UnpaidOrders - 1)
            .And.OnlyContain(p => p.Status == PaymentStatus.Declined && p.DeclineMessage == "Your wallet balance does not cover this order");

        (await shopper.WalletBalanceAsync()).Should().Be(0.00m);
        var debits = await host.WithDbAsync(db => db.WalletTransactions.AsNoTracking()
            .Where(t => t.CustomerId == shopper.Id && t.Kind != WalletTransactionKind.Seed)
            .ToListAsync(Ct));
        debits.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Kind = WalletTransactionKind.Payment, Amount = -100.00m, BalanceAfter = 0.00m, PaymentId = (Guid?)payments.Single(p => p.Status == PaymentStatus.Succeeded).PaymentId,
        });

        // The paid order goes ahead; the two declined ones can still be paid another way while their hold lasts.
        var statuses = await Task.WhenAll(payments.Select(async p => (p.Status, (await host.OrderAsync(p.OrderNumber)).Status)));
        statuses.Should().BeEquivalentTo([
            (PaymentStatus.Succeeded, OrderStatus.Completed),
            (PaymentStatus.Declined, OrderStatus.AwaitingPayment),
            (PaymentStatus.Declined, OrderStatus.AwaitingPayment),
        ]);
    }
}
