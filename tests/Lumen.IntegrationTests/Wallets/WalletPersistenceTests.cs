using System.Net;
using Lumen.Api.Identity;
using Lumen.Api.Payments;
using Lumen.Api.Wallets;
using Lumen.Domain.Payments;
using Lumen.IntegrationTests.Infrastructure;
using Lumen.IntegrationTests.Orders;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lumen.IntegrationTests.Wallets;

/// <summary>The wallet tables on real PostgreSQL, and the opening balances of the seeded and newly registered customers.</summary>
public sealed class WalletPersistenceTests(LumenApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> NewCustomerWithWalletAsync(decimal balance)
    {
        var customerId = await factory.NewCustomerAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<WalletAccounts>().OpenAsync(customerId, balance, Ct)).Should().BeTrue();
        return customerId;
    }

    private static WalletTransaction Debit(Guid customerId, Payment payment, decimal amount) => new()
    {
        CustomerId = customerId, Kind = WalletTransactionKind.Payment, Amount = -amount, BalanceAfter = 0m,
        OrderId = payment.OrderId, PaymentId = payment.Id, CreatedUtc = OrderingTestData.Now,
    };

    [Fact]
    [Trait("BR", "BR-PAY-06")]
    public async Task BR_PAY_06_the_seeded_customer_starts_with_2000_dollars()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var customer = await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync("customer@lumengoods.test");
        var db = scope.ServiceProvider.GetRequiredService<Lumen.Api.Infrastructure.Data.LumenDbContext>();

        (await db.Wallets.SingleAsync(w => w.CustomerId == customer!.Id, Ct)).Balance.Should().Be(2000.00m);
        var seed = await db.WalletTransactions.Where(t => t.CustomerId == customer!.Id && t.Kind == WalletTransactionKind.Seed).ToListAsync(Ct);
        seed.Should().ContainSingle().Which.Amount.Should().Be(2000.00m);
    }

    [Fact]
    [Trait("BR", "BR-PAY-06")]
    public async Task BR_PAY_06_a_newly_registered_customer_starts_with_500_dollars()
    {
        var email = $"new-{Guid.NewGuid():N}@example.test";
        var session = factory.CreateSession();
        await session.StartAsync();

        var response = await session.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "Shopper!2026"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var customer = await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(email);
        var db = scope.ServiceProvider.GetRequiredService<Lumen.Api.Infrastructure.Data.LumenDbContext>();
        (await db.Wallets.SingleAsync(w => w.CustomerId == customer!.Id, Ct)).Balance.Should().Be(500.00m);
        (await db.WalletTransactions.SingleAsync(t => t.CustomerId == customer!.Id, Ct))
            .Should().BeEquivalentTo(new { Kind = WalletTransactionKind.Seed, Amount = 500.00m, BalanceAfter = 500.00m });
    }

    [Fact]
    [Trait("BR", "BR-PAY-06")]
    public async Task BR_PAY_06_opening_a_wallet_twice_changes_nothing()
    {
        var customerId = await NewCustomerWithWalletAsync(500.00m);

        await using var scope = factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<WalletAccounts>().OpenAsync(customerId, 9999.00m, Ct)).Should().BeFalse();

        var db = scope.ServiceProvider.GetRequiredService<Lumen.Api.Infrastructure.Data.LumenDbContext>();
        (await db.Wallets.SingleAsync(w => w.CustomerId == customerId, Ct)).Balance.Should().Be(500.00m);
        (await db.WalletTransactions.CountAsync(t => t.CustomerId == customerId, Ct)).Should().Be(1);
    }

    [Fact]
    [Trait("BR", "BR-PAY-06")]
    public async Task BR_PAY_06_the_balance_never_goes_negative()
    {
        var customerId = await NewCustomerWithWalletAsync(100.00m);

        await using var db = factory.NewContext();
        var debit = () => db.Wallets.Where(w => w.CustomerId == customerId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Balance, w => w.Balance - 100.01m), Ct);

        (await FluentActions.Awaiting(debit).Should().ThrowAsync<Npgsql.PostgresException>())
            .Which.ConstraintName.Should().Be("CK_Wallets_BalanceNotNegative");
        (await db.Wallets.AsNoTracking().SingleAsync(w => w.CustomerId == customerId, Ct)).Balance.Should().Be(100.00m);
    }

    [Fact]
    [Trait("BR", "BR-PAY-06")]
    public async Task BR_PAY_06_a_wallet_payment_debits_once()
    {
        var order = await factory.NewSavedOrderAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<WalletAccounts>().OpenAsync(order.CustomerId, 500.00m, Ct);
        }

        var payment = OrderingTestData.NewPayment(order.Id, PaymentStatus.Succeeded, PaymentMethod.Wallet);
        await factory.SaveAsync(payment, Debit(order.CustomerId, payment, 46.95m));

        (await factory.SaveRejectedAsync(Debit(order.CustomerId, payment, 46.95m)))
            .ShouldBeUniqueViolation("IX_WalletTransactions_PaymentId_Payment");
    }

    [Fact]
    [Trait("BR", "BR-ORD-03")]
    public async Task BR_ORD_03_a_refund_credits_the_wallet_once()
    {
        var order = await factory.NewSavedOrderAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<WalletAccounts>().OpenAsync(order.CustomerId, 500.00m, Ct);
        }

        var payment = OrderingTestData.NewPayment(order.Id, PaymentStatus.Succeeded, PaymentMethod.Wallet);
        var refund = new Lumen.Api.Orders.Refund
        {
            OrderId = order.Id, PaymentId = payment.Id, Amount = 39.96m, Reason = "Not supplied", CreatedUtc = OrderingTestData.Now,
        };
        WalletTransaction Credit() => new()
        {
            CustomerId = order.CustomerId, Kind = WalletTransactionKind.Refund, Amount = 39.96m, BalanceAfter = 539.96m,
            OrderId = order.Id, RefundId = refund.Id, CreatedUtc = OrderingTestData.Now,
        };
        await factory.SaveAsync(payment, refund, Credit());

        (await factory.SaveRejectedAsync(Credit())).ShouldBeUniqueViolation("IX_WalletTransactions_RefundId_Refund");
    }

    [Fact]
    [Trait("BR", "BR-PAY-06")]
    public async Task BR_PAY_06_a_transaction_amount_matches_its_kind()
    {
        var order = await factory.NewSavedOrderAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<WalletAccounts>().OpenAsync(order.CustomerId, 500.00m, Ct);
        }

        var payment = OrderingTestData.NewPayment(order.Id, PaymentStatus.Succeeded, PaymentMethod.Wallet);
        await factory.SaveAsync(payment);

        var positiveDebit = Debit(order.CustomerId, payment, 46.95m);
        positiveDebit.Amount = 46.95m;
        (await factory.SaveRejectedAsync(positiveDebit)).ShouldBeCheckViolation("CK_WalletTransactions_KindMatchesAmount");

        var secondSeed = new WalletTransaction
        {
            CustomerId = order.CustomerId, Kind = WalletTransactionKind.Seed, Amount = 1.00m, BalanceAfter = 501.00m, CreatedUtc = OrderingTestData.Now,
        };
        (await factory.SaveRejectedAsync(secondSeed)).ShouldBeUniqueViolation("IX_WalletTransactions_CustomerId_Seed");
    }
}
