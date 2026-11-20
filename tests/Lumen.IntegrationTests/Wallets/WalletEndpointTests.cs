using System.Net;
using Lumen.Api.Identity;
using Lumen.Api.Payments;
using Lumen.Api.Wallets;
using Lumen.Domain.Payments;
using Lumen.IntegrationTests.Carts;
using Lumen.IntegrationTests.Catalog;
using Lumen.IntegrationTests.Infrastructure;
using Lumen.IntegrationTests.Orders;
using Microsoft.EntityFrameworkCore;

namespace Lumen.IntegrationTests.Wallets;

/// <summary><c>GET /api/v1/wallet</c>: the signed-in customer's own balance and recent transactions (BR-PAY-06).</summary>
public sealed class WalletEndpointTests(LumenApiFactory factory)
{
    private const string WalletUrl = "/api/v1/wallet";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("BR", "BR-PAY-06")]
    public async Task BR_PAY_06_a_new_customer_sees_the_500_dollar_opening_balance()
    {
        var session = factory.CreateSession();
        await session.RegisterAsync();

        var response = await session.GetAsync(WalletUrl);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var wallet = await response.ReadAsync<WalletDto>();
        wallet.Balance.Should().Be(500.00m);
        wallet.Currency.Should().Be("USD");
        wallet.RecentTransactions.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Kind = WalletTransactionKind.Seed, Amount = 500.00m, BalanceAfter = 500.00m, OrderNumber = (string?)null,
        });
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("\"balance\":500.00").And.Contain("\"kind\":\"seed\"");
    }

    [Fact]
    [Trait("BR", "BR-PAY-06")]
    public async Task BR_PAY_06_payments_are_listed_newest_first_with_their_order_number()
    {
        var session = factory.CreateSession();
        var email = await session.RegisterAsync();
        var customerId = await factory.CustomerIdAsync(email);
        var product = await factory.NewProductAsync();
        var order = OrderingTestData.NewOrder(customerId, product.Id);
        var payment = OrderingTestData.NewPayment(order.Id, PaymentStatus.Succeeded, PaymentMethod.Wallet);
        var debit = new WalletTransaction
        {
            CustomerId = customerId, Kind = WalletTransactionKind.Payment, Amount = -44.95m, BalanceAfter = 455.05m,
            OrderId = order.Id, PaymentId = payment.Id, CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(1),
        };
        await factory.SaveAsync(order, payment, debit);
        await factory.WithDbAsync(db => db.Wallets.Where(w => w.CustomerId == customerId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Balance, 455.05m), Ct));

        var wallet = await (await session.GetAsync(WalletUrl)).ReadAsync<WalletDto>();

        wallet.Balance.Should().Be(455.05m);
        wallet.RecentTransactions.Select(t => (t.Kind, t.Amount, t.OrderNumber)).Should().Equal(
            (WalletTransactionKind.Payment, -44.95m, order.Number),
            (WalletTransactionKind.Seed, 500.00m, (string?)null));
    }

    [Fact]
    [Trait("BR", "BR-PAY-06")]
    public async Task BR_PAY_06_a_customer_only_ever_sees_their_own_wallet()
    {
        var first = factory.CreateSession();
        var firstEmail = await first.RegisterAsync();
        var second = factory.CreateSession();
        await second.RegisterAsync();
        var firstId = await factory.CustomerIdAsync(firstEmail);
        await factory.WithDbAsync(db => db.Wallets.Where(w => w.CustomerId == firstId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Balance, 123.45m), Ct));

        (await (await second.GetAsync($"{WalletUrl}?customerId={firstId}")).ReadAsync<WalletDto>()).Balance.Should().Be(500.00m);
        (await (await first.GetAsync(WalletUrl)).ReadAsync<WalletDto>()).Balance.Should().Be(123.45m);
    }

    [Fact]
    [Trait("BR", "BR-PAY-06")]
    public async Task BR_PAY_06_a_customer_whose_wallet_was_not_opened_at_registration_gets_it_on_first_look()
    {
        var session = factory.CreateSession();
        var customerId = await factory.CustomerIdAsync(await session.RegisterAsync());
        // As if opening the wallet had failed during registration.
        await factory.WithDbAsync(async db =>
        {
            await db.WalletTransactions.Where(t => t.CustomerId == customerId).ExecuteDeleteAsync(Ct);
            await db.Wallets.Where(w => w.CustomerId == customerId).ExecuteDeleteAsync(Ct);
        });

        var first = await (await session.GetAsync(WalletUrl)).ReadAsync<WalletDto>();
        var again = await (await session.GetAsync(WalletUrl)).ReadAsync<WalletDto>();

        first.Balance.Should().Be(500.00m);
        first.RecentTransactions.Should().ContainSingle().Which.Kind.Should().Be(WalletTransactionKind.Seed);
        again.Should().BeEquivalentTo(first, "opening is idempotent");
    }

    [Fact]
    public async Task Anonymous_callers_get_401()
    {
        var anonymous = factory.CreateSession();
        await anonymous.StartAsync();

        (await anonymous.GetAsync(WalletUrl)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_account_without_a_wallet_gets_404()
    {
        var admin = await factory.SignedInAsync(Roles.Admin);

        var response = await admin.GetAsync(WalletUrl);

        await response.ShouldBeProblemAsync(HttpStatusCode.NotFound);
        (await factory.WithDbResultAsync(db => db.Wallets.CountAsync(w => db.Users.Any(u => u.Id == w.CustomerId && u.Email == "admin@lumengoods.test"), Ct)))
            .Should().Be(0, "staff accounts never get a wallet");
    }
}
