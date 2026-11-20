using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Infrastructure.Telemetry;
using Lumen.Api.Orders;
using Lumen.Api.Partners;
using Lumen.Api.Wallets;
using Lumen.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Lumen.Api.Payments;

/// <summary>What one run of the refund job did.</summary>
public sealed record RefundRunReport(int Claimed, int Succeeded, int Failed);

/// <summary>
/// BR-ORD-03: pays out recorded refunds to the original payment method. Refunds are recorded (Pending, with their
/// parts) by whoever rejects the lines; this job sends them, outside that transaction:
/// <list type="bullet">
/// <item>it claims due refunds (Pending, or Failed and due for a retry) with <c>FOR UPDATE SKIP LOCKED</c>, so two
/// runs or two instances never take the same one, and leases them for <see cref="PaymentJobsOptions.RefundLease"/>;</item>
/// <item>card: the gateway refund keyed by the refund id, outside any transaction; a repeat after a crash returns the
/// first result, so nothing is paid out twice;</item>
/// <item>wallet: the credit and its wallet transaction in one transaction (the transaction is unique per refund);</item>
/// <item>then Succeeded, or Failed with a growing retry delay (same refund id), and a payment event for the admin timeline.</item>
/// </list>
/// <see cref="Order.RefundedTotal"/> already counts every recorded refund whatever its status (a Failed one is still
/// owed), so paying a refund out changes no order amounts: they stay consistent by construction.
/// </summary>
internal sealed class RefundJob(
    IServiceScopeFactory scopes,
    IOptionsMonitor<PaymentJobsOptions> options,
    IOptionsMonitor<JobsOptions> jobs,
    TimeProvider time,
    ILogger<RefundJob> logger) : BackgroundService
{
    private readonly SemaphoreSlim _running = new(1, 1);

    public async Task<RefundRunReport> RunOnceAsync(CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken);
        try
        {
            List<Guid> claimed;
            await using (var scope = scopes.CreateAsyncScope())
            {
                claimed = await ClaimAsync(scope.ServiceProvider.GetRequiredService<LumenDbContext>(), cancellationToken);
            }

            int succeeded = 0, failed = 0;
            foreach (var refundId in claimed)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var status = await scope.ServiceProvider.GetRequiredService<RefundExecutor>().ExecuteAsync(refundId, cancellationToken);
                    succeeded += status == RefundStatus.Succeeded ? 1 : 0;
                    failed += status == RefundStatus.Failed ? 1 : 0;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // Left Pending under its lease: picked up again when the lease runs out.
                    logger.LogError(ex, "Refund {Refund} could not be executed; retrying after its lease", refundId);
                }
            }

            return new RefundRunReport(claimed.Count, succeeded, failed);
        }
        finally
        {
            _running.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!jobs.CurrentValue.Enabled)
        {
            logger.LogInformation("Refund job is disabled (Jobs:Enabled=false)");
            return;
        }

        await Task.Yield();
        using var timer = new PeriodicTimer(jobs.CurrentValue.PollInterval, time);
        try
        {
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Refund run failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    public override void Dispose()
    {
        _running.Dispose();
        base.Dispose();
    }

    /// <summary>Takes due refunds under a lease; a Failed one due for its retry goes back to Pending (same refund id).</summary>
    private async Task<List<Guid>> ClaimAsync(LumenDbContext db, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var leaseUntil = now + options.CurrentValue.RefundLease;
        var pending = nameof(RefundStatus.Pending);
        var retryable = nameof(RefundStatus.Failed);
        _ = OrderTransitions.Refund.Move(RefundStatus.Failed, RefundStatus.Pending); // the retry this claim makes is allowed
        var batch = options.CurrentValue.BatchSize;
        return await db.Database.SqlQuery<Guid>($"""
            WITH due AS (
                SELECT "Id" FROM "Refunds"
                WHERE "Status" IN ({pending}, {retryable}) AND ("NextAttemptUtc" IS NULL OR "NextAttemptUtc" <= {now})
                ORDER BY "CreatedUtc"
                LIMIT {batch}
                FOR UPDATE SKIP LOCKED)
            UPDATE "Refunds" AS r
            SET "Status" = {pending}, "NextAttemptUtc" = {leaseUntil}, "Attempts" = r."Attempts" + 1
            FROM due
            WHERE r."Id" = due."Id"
            RETURNING r."Id" AS "Value"
            """).ToListAsync(cancellationToken);
    }
}

/// <summary>Pays out one claimed refund (see <see cref="RefundJob"/>).</summary>
internal sealed class RefundExecutor(
    LumenDbContext db,
    IPaymentGateway gateway,
    OrderPaymentWriter writer,
    IOptionsMonitor<PaymentJobsOptions> options,
    TimeProvider time,
    LumenMetrics metrics,
    ILogger<RefundExecutor> logger)
{
    public async Task<RefundStatus> ExecuteAsync(Guid refundId, CancellationToken cancellationToken)
    {
        var refund = await db.Refunds.AsNoTracking().SingleAsync(r => r.Id == refundId, cancellationToken);
        if (refund.Status != RefundStatus.Pending)
        {
            return refund.Status;
        }

        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == refund.PaymentId, cancellationToken);
        if (payment.Method == PaymentMethod.Wallet)
        {
            return await CreditWalletAsync(refundId, payment, cancellationToken);
        }

        GatewayRefundResult result;
        try
        {
            // No transaction is open here: the gateway call is payment I/O.
            result = await gateway.RefundAsync(new GatewayRefundRequest(refund.Id, refund.PaymentId, refund.Amount), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Gateway refund {Refund} failed", refundId);
            result = new GatewayRefundResult(false, $"Gateway call failed: {ex.GetType().Name}");
        }

        return await CompleteAsync(refundId, payment, result.Succeeded, result.Error, cancellationToken);
    }

    /// <summary>BR-PAY-06 / BR-ORD-03: the credit, its wallet transaction (unique per refund) and the refund's success, together.</summary>
    private async Task<RefundStatus> CreditWalletAsync(Guid refundId, Payment payment, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var refund = await LockAsync(refundId, cancellationToken);
        if (refund.Status != RefundStatus.Pending)
        {
            return refund.Status;
        }

        var customerId = await db.Orders.Where(o => o.Id == refund.OrderId).Select(o => o.CustomerId).SingleAsync(cancellationToken);
        var amount = refund.Amount;
        var balance = (await db.Database.SqlQuery<decimal>($"""
            UPDATE "Wallets" SET "Balance" = "Balance" + {amount}
            WHERE "CustomerId" = {customerId}
            RETURNING "Balance" AS "Value"
            """).ToListAsync(cancellationToken)).Cast<decimal?>().SingleOrDefault();
        if (balance is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await CompleteAsync(refundId, payment, false, "The customer has no wallet to credit.", cancellationToken);
        }

        var now = time.GetUtcNow();
        db.WalletTransactions.Add(new WalletTransaction
        {
            CustomerId = customerId,
            Amount = amount,
            BalanceAfter = balance.Value,
            Kind = WalletTransactionKind.Refund,
            OrderId = refund.OrderId,
            RefundId = refund.Id,
            CreatedUtc = now,
        });
        await MarkAsync(refund, payment, succeeded: true, error: null, now, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // IX_WalletTransactions_RefundId_Refund: this refund was already credited; never credit it twice.
            await transaction.RollbackAsync(CancellationToken.None);
            logger.LogWarning("Refund {Refund} was already credited to the wallet", refundId);
            return (await db.Refunds.AsNoTracking().SingleAsync(r => r.Id == refundId, cancellationToken)).Status;
        }

        metrics.RefundFinished(PaymentMethod.Wallet, RefundStatus.Succeeded);
        logger.LogInformation("Refund {Refund}: {Amount} USD credited to the wallet, balance now {Balance}", refundId, amount, balance);
        return RefundStatus.Succeeded;
    }

    private async Task<RefundStatus> CompleteAsync(Guid refundId, Payment payment, bool succeeded, string? error, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var refund = await LockAsync(refundId, cancellationToken);
        if (refund.Status != RefundStatus.Pending)
        {
            return refund.Status;
        }

        await MarkAsync(refund, payment, succeeded, error, time.GetUtcNow(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        metrics.RefundFinished(payment.Method, refund.Status);
        if (succeeded)
        {
            logger.LogInformation("Refund {Refund}: {Amount} USD refunded to the card", refundId, refund.Amount);
        }
        else
        {
            logger.LogWarning("Refund {Refund} failed ({Error}); retrying at {Next:o} with the same refund id", refundId, error, refund.NextAttemptUtc);
        }

        return refund.Status;
    }

    private async Task MarkAsync(Refund refund, Payment payment, bool succeeded, string? error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (succeeded)
        {
            refund.Status = OrderTransitions.Refund.Move(refund.Status, RefundStatus.Succeeded);
            refund.CompletedUtc = now;
            refund.NextAttemptUtc = null;
            refund.LastError = null;
            await writer.AddEventAsync(payment, PaymentEventType.Refunded, now, refund.Amount, refund.Reason, cancellationToken);
            return;
        }

        refund.Status = OrderTransitions.Refund.Move(refund.Status, RefundStatus.Failed);
        refund.LastError = error is { Length: > 2000 } ? error[..2000] : error;
        refund.NextAttemptUtc = now + RetryDelay(refund.Attempts);
        await writer.AddEventAsync(payment, PaymentEventType.RefundFailed, now, refund.Amount, error, cancellationToken);
    }

    private TimeSpan RetryDelay(int attempts)
    {
        var settings = options.CurrentValue;
        var delay = settings.RefundRetryDelay * Math.Pow(2, Math.Clamp(attempts - 1, 0, 20));
        return delay < settings.RefundMaxRetryDelay ? delay : settings.RefundMaxRetryDelay;
    }

    /// <summary>Locks the refund row for this transaction and loads it tracked.</summary>
    private async Task<Refund> LockAsync(Guid refundId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "Refunds" WHERE "Id" = {refundId} FOR UPDATE""", cancellationToken);
        return await db.Refunds.SingleAsync(r => r.Id == refundId, cancellationToken);
    }
}
