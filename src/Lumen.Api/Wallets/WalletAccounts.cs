using Lumen.Api.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Lumen.Api.Wallets;

/// <summary>Opens customers' wallets: the seeded customer's on start-up, a new customer's at registration.</summary>
public sealed class WalletAccounts(LumenDbContext db, TimeProvider time)
{
    /// <summary>
    /// Creates the customer's wallet with <paramref name="openingBalance"/> and its <c>Seed</c> transaction, in one SQL
    /// statement, if the customer has none yet. Idempotent and safe under concurrency: a second call (a replayed
    /// registration, a restart re-running the seed) changes nothing, because the insert skips an existing wallet and the
    /// seed transaction is only written when the wallet was created by this statement.
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

        // A zero opening balance writes no seed transaction (amounts are never zero), so 0 rows can still mean "opened".
        return rows > 0 || (openingBalance == 0 && await db.Set<Wallet>().AnyAsync(w => w.CustomerId == customerId, cancellationToken));
    }
}
