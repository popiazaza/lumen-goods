using Lumen.Api.Partners;
using Lumen.Api.Partners.Ports;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Orders;

/// <summary>What one run of the partner order job did.</summary>
/// <param name="Steps">One entry per claimed partner order whose result was applied.</param>
/// <param name="Failed">Claimed partner orders whose result could not be applied (retried after the claim times out).</param>
/// <param name="FlaggedNeedsAttention">Lines flagged for an admin in this run (BR-ORD-04).</param>
internal sealed record PartnerOrderRunReport(IReadOnlyList<PartnerOrderStep> Steps, int Failed, int FlaggedNeedsAttention)
{
    public int Count(PartnerOrderStep step) => Steps.Count(s => s == step);
}

/// <summary>
/// Places and follows partner orders after payment (BR-ORD-01, BR-ORD-03, BR-ORD-04). A thin loop wakes every
/// <see cref="JobsOptions.PollInterval"/> and calls <see cref="RunOnceAsync(CancellationToken)"/>, which:
/// 1. claims the due partner orders of paid orders in one short transaction (<see cref="PartnerOrderProcessor.ClaimDueAsync"/>);
/// 2. calls the partners outside any transaction: sends Pending/Submitted ones, polls accepted tickets. Partners run
///    side by side, each partner's orders one at a time, so a slow partner holds up only its own orders;
/// 3. applies each answer in its own short transaction (<see cref="PartnerOrderProcessor.ApplyAsync"/>);
/// 4. flags accepted lines still undecided after 24 hours for an admin (<see cref="PartnerOrderProcessor.FlagUndecidedAsync"/>).
/// Tests switch the loop off (Jobs:Enabled=false) and call RunOnceAsync with a fake clock.
/// </summary>
internal sealed class PartnerOrderJob(
    IServiceScopeFactory scopes,
    PartnerOrderGateways gateways,
    IOptionsMonitor<JobsOptions> jobs,
    TimeProvider clock,
    ILogger<PartnerOrderJob> logger) : BackgroundService
{
    private readonly SemaphoreSlim _running = new(1, 1);

    public Task<PartnerOrderRunReport> RunOnceAsync(CancellationToken cancellationToken) => RunOnceAsync(null, cancellationToken);

    /// <param name="onlyOrderIds">
    /// Restricts the run to these orders. Integration tests share one database between tests running in parallel, so each
    /// runs the job for its own orders only; production passes null.
    /// </param>
    internal async Task<PartnerOrderRunReport> RunOnceAsync(IReadOnlyCollection<Guid>? onlyOrderIds, CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<PartnerOrderWork> work;
            await using (var scope = scopes.CreateAsyncScope())
            {
                work = await scope.ServiceProvider.GetRequiredService<PartnerOrderProcessor>().ClaimDueAsync(onlyOrderIds, cancellationToken);
            }

            var perPartner = await Task.WhenAll(work
                .GroupBy(w => w.Source, StringComparer.Ordinal)
                .Select(group => RunPartnerAsync([.. group], cancellationToken)));
            var steps = perPartner.SelectMany(p => p.Steps).ToList();
            var failed = perPartner.Sum(p => p.Failed);

            int flagged;
            await using (var scope = scopes.CreateAsyncScope())
            {
                flagged = await scope.ServiceProvider.GetRequiredService<PartnerOrderProcessor>().FlagUndecidedAsync(onlyOrderIds, cancellationToken);
            }

            return new PartnerOrderRunReport(steps, failed, flagged);
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
            logger.LogInformation("Partner order job is disabled (Jobs:Enabled=false)");
            return;
        }

        // Let the host finish starting before the first partner call.
        await Task.Yield();
        using var timer = new PeriodicTimer(jobs.CurrentValue.PollInterval, clock);
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
                    // Partner failures are recorded per partner order; this is the database or a bug. Keep the loop alive.
                    logger.LogError(ex, "Partner order run failed");
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

    private async Task<(List<PartnerOrderStep> Steps, int Failed)> RunPartnerAsync(List<PartnerOrderWork> items, CancellationToken cancellationToken)
    {
        var steps = new List<PartnerOrderStep>(items.Count);
        var failed = 0;
        foreach (var item in items)
        {
            var result = await CallPartnerAsync(item, cancellationToken);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                steps.Add(await scope.ServiceProvider.GetRequiredService<PartnerOrderProcessor>().ApplyAsync(item, result, cancellationToken));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The claim keeps the partner order reserved until it times out; then it is sent again with the same
                // reference and the partner's (repeated) answer is applied then.
                logger.LogError(ex, "Could not apply {Partner}'s {Outcome} answer for partner order {PartnerOrderId}",
                    item.Source, result.Outcome, item.PartnerOrderId);
                failed++;
            }
        }

        return (steps, failed);
    }

    private async Task<PartnerOrderResult> CallPartnerAsync(PartnerOrderWork item, CancellationToken cancellationToken)
    {
        if (gateways.Find(item.Source) is not { } gateway)
        {
            return new PartnerOrderResult(PartnerOrderOutcome.RetryLater, Error: $"No order gateway is registered for partner '{item.Source}'.");
        }

        try
        {
            return item.Poll && gateway.DecidesLater && item.Ticket is { } ticket
                ? await gateway.GetStatusAsync(ticket, cancellationToken)
                : await gateway.SubmitAsync(item.Request, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Gateways turn partner failures into RetryLater themselves; this is a bug, but the order must not get stuck.
            logger.LogError(ex, "Order gateway {Partner} failed unexpectedly", item.Source);
            return new PartnerOrderResult(PartnerOrderOutcome.RetryLater, Error: $"Unexpected error calling {item.Source}: {ex.GetType().Name}.");
        }
    }
}
