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
/// <item>expires orders awaiting payment whose hold is over or whose attempts are used up, when no payment is in
/// progress, releasing their reserved stock. A slow card still running when the hold ends keeps the order.</item>
/// </list>
/// Each payment and order is handled in its own scope and transaction, so one bad row never stops the rest. Tests
/// switch the loop off (<c>Jobs:Enabled=false</c>) and call <see cref="RunOnceAsync"/>.
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

            List<Guid> due;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LumenDbContext>();

                // Served by the (Status, StatusChangedUtc) index. A running payment holds its order in PaymentProcessing;
                // a Failed one leaves it awaiting payment until the gateway settles it.
                due = await db.Payments.AsNoTracking()
                    .Where(p => p.Method == PaymentMethod.Card
                        && ((db.Orders.Any(o => o.Id == p.OrderId && o.Status == OrderStatus.PaymentProcessing)
                                && ((p.Status == PaymentStatus.RequiresAction && p.StatusChangedUtc <= codeExpired)
                                    || (p.Status == PaymentStatus.Error && (p.AutomaticRetryUsed || p.StatusChangedUtc <= retryOver))
                                    || ((p.Status == PaymentStatus.Created || p.Status == PaymentStatus.Processing) && p.StatusChangedUtc <= stuck)))
                            || (p.Status == PaymentStatus.Failed && db.Orders.Any(o => o.Id == p.OrderId && o.Status == OrderStatus.AwaitingPayment))))
                    .OrderBy(p => p.StatusChangedUtc)
                    .Select(p => p.Id)
                    .Take(batch)
                    .ToListAsync(cancellationToken);
            }

            foreach (var paymentId in due)
            {
                await InScopeAsync(services => services.GetRequiredService<PaymentHousekeeping>().SettleIfDueAsync(paymentId, cancellationToken),
                    "settling payment", paymentId, cancellationToken);
            }

            List<Guid> expiring;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LumenDbContext>();
                var maxAttempts = window.MaxAttempts;
                expiring = await db.Orders.AsNoTracking()
                    .Where(o => o.Status == OrderStatus.AwaitingPayment && (o.HoldExpiresUtc <= now || o.FailedPaymentAttempts >= maxAttempts))
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
