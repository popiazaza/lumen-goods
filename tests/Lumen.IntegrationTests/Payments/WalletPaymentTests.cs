using System.Net;
using Lumen.Api.Orders;
using Lumen.Api.Payments;
using Lumen.Api.Wallets;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Lumen.IntegrationTests.Catalog;
using Lumen.IntegrationTests.Checkout;
using Microsoft.EntityFrameworkCore;

namespace Lumen.IntegrationTests.Payments;

/// <summary>Wallet payments on the real API and database (BR-PAY-06, BR-PAY-01).</summary>
public sealed class WalletPaymentTests(CheckoutHost host) : IClassFixture<CheckoutHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One own product at $87.97: with 8 % tax ($7.04) and Standard shipping ($4.99) the order is exactly $100.00.</summary>
    private const decimal HundredDollarPrice = 87.97m;

    private Task<List<WalletTransaction>> TransactionsAsync(Guid customerId) => host.WithDbAsync(db =>
        db.WalletTransactions.AsNoTracking().Where(t => t.CustomerId == customerId && t.Kind != WalletTransactionKind.Seed).ToListAsync(Ct));

    [Fact]
    [Trait("BR", "BR-PAY-06")]
    public async Task BR_PAY_06_the_wallet_pays_the_order_total_with_one_debit_and_its_log_entry()
    {
        var shopper = await host.NewShopperAsync();
        await shopper.AddToCartAsync(await host.OwnProductAsync(HundredDollarPrice), 1);
        var order = await shopper.PlaceOrderAsync();
        order.Totals.Total.Should().Be(100.00m);

        var response = await shopper.PayByWalletAsync(order.Number);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var payment = await response.ReadAsync<PaymentStatusDto>();
        (payment.Method, payment.Status, payment.Amount, payment.CardLast4).Should().Be((PaymentMethod.Wallet, PaymentStatus.Succeeded, 100.00m, (string?)null));
        (await shopper.WalletBalanceAsync()).Should().Be(400.00m);
        (await TransactionsAsync(shopper.Id)).Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Kind = WalletTransactionKind.Payment, Amount = -100.00m, BalanceAfter = 400.00m, PaymentId = (Guid?)payment.PaymentId,
        });
    }

    [Fact]
    [Trait("BR", "BR-PAY-06")]
    public async Task BR_PAY_06_a_balance_below_the_total_declines_and_leaves_the_wallet_untouched()
    {
        var shopper = await host.NewShopperAsync();
        await shopper.SetWalletBalanceAsync(100.00m);
        await shopper.AddToCartAsync(await host.OwnProductAsync(106.49m), 1); // $120.00 with tax and shipping
        var order = await shopper.PlaceOrderAsync();
        order.Totals.Total.Should().Be(120.00m);

        var payment = await (await shopper.PayByWalletAsync(order.Number)).ReadAsync<PaymentStatusDto>();

        (payment.Status, payment.DeclineMessage, payment.RetryMode).Should().Be(
            (PaymentStatus.Declined, "Your wallet balance does not cover this order", PaymentRetryMode.AnyMethod));
        (await shopper.WalletBalanceAsync()).Should().Be(100.00m);
        (await TransactionsAsync(shopper.Id)).Should().BeEmpty();
        (await shopper.PayByCardAndWaitAsync(order.Number, "4111111111111111")).Status.Should().Be(PaymentStatus.Succeeded);
    }

    [Fact]
    [Trait("BR", "BR-PAY-06")]
    public async Task BR_PAY_06_two_100_dollar_orders_paid_at_the_same_moment_from_150_dollars_one_succeeds()
    {
        var shopper = await host.NewShopperAsync();
        await shopper.SetWalletBalanceAsync(150.00m);
        await shopper.AddToCartAsync(await host.OwnProductAsync(HundredDollarPrice), 1);
        var first = await shopper.PlaceOrderAsync();
        var second = await shopper.PlaceOrderAsync(); // the cart is unchanged until an order is paid

        var payments = await Task.WhenAll(
            (await Task.WhenAll(shopper.PayByWalletAsync(first.Number), shopper.PayByWalletAsync(second.Number)))
                .Select(r => r.ReadAsync<PaymentStatusDto>()));

        payments.Select(p => p.Status).Should().BeEquivalentTo([PaymentStatus.Succeeded, PaymentStatus.Declined]);
        (await shopper.WalletBalanceAsync()).Should().Be(50.00m);
        (await TransactionsAsync(shopper.Id)).Should().ContainSingle().Which.Amount.Should().Be(-100.00m);
    }

    [Fact]
    [Trait("BR", "BR-PAY-01")]
    [Trait("BR", "BR-PAY-06")]
    public async Task BR_PAY_01_one_order_paid_from_the_wallet_in_two_tabs_at_once_leaves_exactly_one_payment()
    {
        var shopper = await host.NewShopperAsync();
        await shopper.AddToCartAsync(await host.OwnProductAsync(HundredDollarPrice), 1);
        var order = await shopper.PlaceOrderAsync();

        var responses = await Task.WhenAll(shopper.PayByWalletAsync(order.Number), shopper.PayByWalletAsync(order.Number));

        responses.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.Accepted, HttpStatusCode.Conflict]);
        var saved = await host.OrderAsync(order.Number);
        saved.Payments.Should().ContainSingle().Which.Status.Should().Be(PaymentStatus.Succeeded);
        saved.Status.Should().Be(OrderStatus.Completed);
        (await shopper.WalletBalanceAsync()).Should().Be(400.00m);
        (await TransactionsAsync(shopper.Id)).Should().ContainSingle();
    }
}
