using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Partners;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Payments;

/// <summary>What one run of the payment window job did.</summary>
/// <param name="PaymentsChecked">Payments looked at: expired codes, errors past their retry window, stuck charges, Failed payments.</param>
/// <param name="OrdersExpired">Orders moved to Expired, their reserved stock released.</param>
public sealed record PaymentWindowReport(int PaymentsChecked, int OrdersExpired);

/// <summary>
/// The hold-expiry and payment housekeeping job (BR-STOCK-01, BR-PAY-03, BR-PAY-05): wakes every
/// <c>Jobs:PollInterval</c> and, through <see cref="PaymentHousekeeping"/>,
/// <list type="number">
/// <item>closes payments that no longer hold their order: a one-time code nobody entered in time, a gateway error
/// whose one automatic retry can no longer run, a charge stuck Created or Processing (its worker died: the gateway's
/// status decides), and Failed payments (the gateway's status settles them);</item>
/// <item>expires orders awaiting payment whose hold is over or whose attempts are used up, when no payment holds
/// them, releasing their reserved stock. A slow card still running when the hold ends keeps the order, and so does a
/// Failed payment until the gateway settles it.</item>
/// </list>
/// Each payment and order is handled in its own scope and transaction, so one bad row never stops the rest. Payments
/// the gateway cannot answer for stay due, so the payment sweep pages through the due set with a cursor instead of
/// always taking the oldest batch: a gateway status outage over more payments than one batch then only slows the sweep
/// down, and never stops it from reaching the payments behind them. Tests switch the loop off
/// (<c>Jobs:Enabled=false</c>) and call <see cref="RunOnceAsync"/>.
/// </summary>
internal sealed class PaymentWindowJob(
    IServiceScopeFactory scopes,
    PaymentWindowPolicy window,
    IOptionsMonitor<PaymentOptions> payments,
    IOptionsMonitor<PaymentJobsOptions> options,
    IOptionsMonitor<JobsOptions> jobs,
    TimeProvider time,
    ILogger<PaymentWindowJob> logger) : BackgroundService
{
    private readonly SemaphoreSlim _running = new(1, 1);

    /// <summary>
    /// Where the next payment sweep starts: after the last payment of a full batch, or from the oldest when the last
    /// sweep reached the end. Only read and written under <see cref="_running"/>; a restart simply starts from the oldest.
    /// </summary>
    private (DateTimeOffset StatusChangedUtc, Guid Id)? _sweepCursor;

    public async Task<PaymentWindowReport> RunOnceAsync(CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            var batch = options.CurrentValue.BatchSize;
            var codeExpired = now - payments.CurrentValue.OneTimeCodeLifetime;
            var retryOver = now - payments.CurrentValue.AutomaticRetryWindow;
            var stuck = now - (payments.CurrentValue.SlowCardDelay + options.CurrentValue.StaleChargeGrace);

            List<(Guid Id, DateTimeOffset StatusChangedUtc)> due;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LumenDbContext>();

                // Served by the (Status, StatusChangedUtc) index. A running payment holds its order in PaymentProcessing;
                // a Failed one leaves it awaiting payment until the gateway settles it.
                var query = db.Payments.AsNoTracking()
                    .Where(p => p.Method == PaymentMethod.Card
                        && ((db.Orders.Any(o => o.Id == p.OrderId && o.Status == OrderStatus.PaymentProcessing)
                                && ((p.Status == PaymentStatus.RequiresAction && p.StatusChangedUtc <= codeExpired)
                                    || (p.Status == PaymentStatus.Error && (p.AutomaticRetryUsed || p.StatusChangedUtc <= retryOver))
                                    || ((p.Status == PaymentStatus.Created || p.Status == PaymentStatus.Processing) && p.StatusChangedUtc <= stuck)))
                            || (p.Status == PaymentStatus.Failed && db.Orders.Any(o => o.Id == p.OrderId && o.Status == OrderStatus.AwaitingPayment))));
                if (_sweepCursor is (var afterChanged, var afterId))
                {
                    // Row comparison (StatusChangedUtc, Id) > cursor, matching the sweep's order.
                    query = query.Where(p => EF.Functions.GreaterThan(
                        ValueTuple.Create(p.StatusChangedUtc, p.Id), ValueTuple.Create(afterChanged, afterId)));
                }

                due = (await query
                    .OrderBy(p => p.StatusChangedUtc).ThenBy(p => p.Id)
                    .Select(p => new { p.Id, p.StatusChangedUtc })
                    .Take(batch)
                    .ToListAsync(cancellationToken))
                    .ConvertAll(p => (p.Id, p.StatusChangedUtc));
            }

            // A full batch may have more behind it: continue after it next time. A short one reached the end, so the next
            // sweep starts again from the oldest, which also picks up payments that became due behind the cursor.
            _sweepCursor = due.Count == batch ? (due[^1].StatusChangedUtc, due[^1].Id) : null;

            foreach (var (paymentId, _) in due)
            {
                await InScopeAsync(services => services.GetRequiredService<PaymentHousekeeping>().SettleIfDueAsync(paymentId, cancellationToken),
                    "settling payment", paymentId, cancellationToken);
            }

            List<Guid> expiring;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LumenDbContext>();
                var maxAttempts = window.MaxAttempts;

                // The rule is PaymentWindowPolicy.MustExpireOrder, which ExpireIfDueAsync applies to each order. This
                // query only picks the candidates, and it must leave out what the policy holds for a Failed payment
                // (BR-PAY-05): otherwise those orders, oldest first, could fill every batch during a gateway status
                // outage and no other order would ever expire. "Has a Failed payment" is the policy's "latest payment
                // is Failed": at most one payment per order is not final (IX_Payments_OrderId_NotFinal) and Failed is
                // not final, so a Failed payment is always the order's latest. A payment in progress needs no filter:
                // its order is PaymentProcessing (the claim), not AwaitingPayment.
                expiring = await db.Orders.AsNoTracking()
                    .Where(o => o.Status == OrderStatus.AwaitingPayment && (o.HoldExpiresUtc <= now || o.FailedPaymentAttempts >= maxAttempts)
                        && !db.Payments.Any(p => p.OrderId == o.Id && p.Status == PaymentStatus.Failed))
                    .OrderBy(o => o.HoldExpiresUtc)
                    .Select(o => o.Id)
                    .Take(batch)
                    .ToListAsync(cancellationToken);
            }

            var expired = 0;
            foreach (var orderId in expiring)
            {
                await InScopeAsync(async services =>
                {
                    if (await services.GetRequiredService<PaymentHousekeeping>().ExpireIfDueAsync(orderId, cancellationToken))
                    {
                        expired++;
                    }
                }, "expiring order", orderId, cancellationToken);
            }

            return new PaymentWindowReport(due.Count, expired);
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
            logger.LogInformation("Payment window job is disabled (Jobs:Enabled=false)");
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
                    logger.LogError(ex, "Payment window run failed");
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

    private async Task InScopeAsync(Func<IServiceProvider, Task> work, string what, Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await work(scope.ServiceProvider);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Payment window job: {What} {Id} failed; retrying on the next run", what, id);
        }
    }
}
