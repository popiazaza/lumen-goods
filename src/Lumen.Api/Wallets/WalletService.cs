using Lumen.Api.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Wallets;

/// <summary>
/// Opens customers' wallets (BR-PAY-06, A-38). Opening is idempotent and lazy: a customer's wallet is created the first
/// time anything needs it, so no single step (registration included) has to succeed for the customer to have one.
/// <list type="bullet">
/// <item>registration calls <see cref="TryEnsureOpenAsync"/> (best effort, never fails the registration);</item>
/// <item>every first use calls <see cref="EnsureOpenAsync"/>: <c>GET /wallet</c> and paying by wallet, before reading
/// or debiting the balance;</item>
/// <item>start-up seeding opens the seeded customer's with <see cref="WalletOptions.SeededCustomerBalance"/>.</item>
/// </list>
/// </summary>
public sealed class WalletService(LumenDbContext db, TimeProvider time, IOptions<WalletOptions> options, ILogger<WalletService> logger)
{
    /// <summary>
    /// Opens the customer's wallet with <see cref="WalletOptions.NewCustomerBalance"/> if it has none; otherwise changes
    /// nothing. Safe to call on every wallet use and concurrently.
    /// </summary>
    /// <returns>True when this call created the wallet.</returns>
    public Task<bool> EnsureOpenAsync(Guid customerId, CancellationToken cancellationToken) =>
        OpenAsync(customerId, options.Value.NewCustomerBalance, cancellationToken);

    /// <summary>
    /// <see cref="EnsureOpenAsync"/> for registration: never throws (it is logged), so a database hiccup cannot fail a
    /// registration whose account already exists; the first wallet use opens the wallet instead. Not cancellable, so a
    /// client that disconnects mid-registration still gets its wallet.
    /// </summary>
    public async Task TryEnsureOpenAsync(Guid customerId)
    {
        try
        {
            await EnsureOpenAsync(customerId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open the wallet of new customer {CustomerId}; it opens on first use", customerId);
        }
    }

    /// <summary>
    /// Creates the customer's wallet with <paramref name="openingBalance"/> and its <c>Seed</c> transaction, in one SQL
    /// statement, if the customer has none yet. Idempotent and safe under concurrency: the insert skips an existing
    /// wallet (its primary key is the customer id) and the seed transaction is only written when this statement created
    /// the wallet, so a replayed registration, a second first use or a restart re-running the seed changes nothing.
    /// </summary>
    /// <returns>True when this call created the wallet.</returns>
    public async Task<bool> OpenAsync(Guid customerId, decimal openingBalance, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(openingBalance);
        var now = time.GetUtcNow();
        var seedId = Guid.CreateVersion7(now);
        var seed = nameof(WalletTransactionKind.Seed);

        var rows = await db.Database.ExecuteSqlAsync($"""
            WITH opened AS (
                INSERT INTO "Wallets" ("CustomerId", "Balance", "CreatedUtc")
                VALUES ({customerId}, {openingBalance}, {now})
                ON CONFLICT ("CustomerId") DO NOTHING
                RETURNING "CustomerId", "Balance")
            INSERT INTO "WalletTransactions" ("Id", "CustomerId", "Amount", "BalanceAfter", "Kind", "CreatedUtc")
            SELECT {seedId}, "CustomerId", "Balance", "Balance", {seed}, {now}
            FROM opened
            WHERE "Balance" > 0
            """, cancellationToken);

        // The count is the seed rows written. A zero opening balance writes none (amounts are never zero), so for it the
        // answer is only whether the wallet exists now; the result is informational (logging) either way.
        return rows > 0 || (openingBalance == 0 && await db.Set<Wallet>().AnyAsync(w => w.CustomerId == customerId, cancellationToken));
    }
}
