using Lumen.Api.Partners;
using Lumen.Api.Partners.Ports;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Orders;

/// <summary>What one run of the partner order job did.</summary>
/// <param name="Steps">One entry per claimed partner order whose result was applied.</param>
/// <param name="Failed">Claimed partner orders whose result could not be applied (retried after the claim times out).</param>
/// <param name="FlaggedNeedsAttention">Lines flagged for an admin in this run because an accepted order stayed undecided (BR-ORD-04).</param>
internal sealed record PartnerOrderRunReport(IReadOnlyList<PartnerOrderStep> Steps, int Failed, int FlaggedNeedsAttention)
{
    public static readonly PartnerOrderRunReport Empty = new([], 0, 0);

    public int Count(PartnerOrderStep step) => Steps.Count(s => s == step);

    public static PartnerOrderRunReport Combine(IEnumerable<PartnerOrderRunReport> reports)
    {
        var all = reports.ToList();
        return new PartnerOrderRunReport([.. all.SelectMany(r => r.Steps)], all.Sum(r => r.Failed), all.Sum(r => r.FlaggedNeedsAttention));
    }
}

/// <summary>
/// Places and follows partner orders after payment (BR-ORD-01, BR-ORD-03, BR-ORD-04). Each partner has its own loop,
/// waking every <see cref="JobsOptions.PollInterval"/>, so a slow or failing partner only ever delays its own orders.
/// One run for a partner (<see cref="RunPartnerOnceAsync"/>), repeated while it has due work, at most
/// <see cref="PartnerOrdersOptions.BatchSize"/> partner orders and for at most <see cref="PartnerOrdersOptions.RunBudget"/>:
/// 1. claims the partner's next due partner order of a paid order in one short transaction (<see cref="PartnerOrderProcessor.ClaimNextAsync"/>);
/// 2. calls the partner outside any transaction: sends a Pending/Submitted one, or polls an accepted ticket;
/// 3. applies the answer in its own short transaction (<see cref="PartnerOrderProcessor.ApplyAsync"/>);
/// then flags the partner's accepted lines still undecided after 24 hours (<see cref="PartnerOrderProcessor.FlagUndecidedAsync"/>).
/// Tests switch the loops off (Jobs:Enabled=false) and call <see cref="RunOnceAsync(CancellationToken)"/> with a fake clock.
/// </summary>
internal sealed class PartnerOrderJob(
    IServiceScopeFactory scopes,
    PartnerOrderGateways gateways,
    IOptionsMonitor<PartnerOrdersOptions> options,
    IOptionsMonitor<JobsOptions> jobs,
    TimeProvider clock,
    ILogger<PartnerOrderJob> logger) : BackgroundService
{
    // One gate per partner: a partner's runs never overlap in this process, and never wait for another partner's.
    private readonly Dictionary<string, SemaphoreSlim> _gates =
        gateways.All.ToDictionary(g => g.Partner, _ => new SemaphoreSlim(1, 1), StringComparer.OrdinalIgnoreCase);

    /// <summary>One run for every partner, side by side.</summary>
    public Task<PartnerOrderRunReport> RunOnceAsync(CancellationToken cancellationToken) => RunOnceAsync(null, cancellationToken);

    /// <param name="onlyOrderIds">
    /// Restricts the run to these orders. Integration tests share one database between tests running in parallel, so each
    /// runs the job for its own orders only; production passes null.
    /// </param>
    internal async Task<PartnerOrderRunReport> RunOnceAsync(IReadOnlyCollection<Guid>? onlyOrderIds, CancellationToken cancellationToken) =>
        PartnerOrderRunReport.Combine(await Task.WhenAll(gateways.All.Select(g => RunPartnerOnceAsync(g.Partner, onlyOrderIds, cancellationToken))));

    /// <summary>One run for one partner, independent of every other partner's.</summary>
    internal async Task<PartnerOrderRunReport> RunPartnerOnceAsync(string partner, IReadOnlyCollection<Guid>? onlyOrderIds, CancellationToken cancellationToken)
    {
        if (gateways.Find(partner) is not { } gateway || !_gates.TryGetValue(partner, out var gate))
        {
            return PartnerOrderRunReport.Empty;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            var settings = options.CurrentValue;
            var started = clock.GetUtcNow();
            var steps = new List<PartnerOrderStep>();
            var failed = 0;

            // One partner order at a time: nothing is claimed that this run might not get to, so nothing waits for a
            // claim to time out when the budget runs out.
            while (steps.Count + failed < settings.BatchSize && clock.GetUtcNow() - started < settings.RunBudget)
            {
                PartnerOrderWork? item;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    item = await scope.ServiceProvider.GetRequiredService<PartnerOrderProcessor>().ClaimNextAsync(partner, onlyOrderIds, cancellationToken);
                }

                if (item is null)
                {
                    break;
                }

                var result = await CallPartnerAsync(gateway, item, cancellationToken);
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    steps.Add(await scope.ServiceProvider.GetRequiredService<PartnerOrderProcessor>().ApplyAsync(item, result, cancellationToken));
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // The claim keeps the partner order reserved until it times out, and marked as an unknown outcome;
                    // then it is sent again with the same reference and the partner's (repeated) answer is applied then.
                    logger.LogError(ex, "Could not apply {Partner}'s {Outcome} answer for partner order {PartnerOrderId}",
                        partner, result.Outcome, item.PartnerOrderId);
                    failed++;
                }
            }

            int flagged;
            await using (var scope = scopes.CreateAsyncScope())
            {
                flagged = await scope.ServiceProvider.GetRequiredService<PartnerOrderProcessor>().FlagUndecidedAsync(partner, onlyOrderIds, cancellationToken);
            }

            return new PartnerOrderRunReport(steps, failed, flagged);
        }
        finally
        {
            gate.Release();
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
        await Task.WhenAll(gateways.All.Select(g => LoopAsync(g.Partner, stoppingToken)));
    }

    public override void Dispose()
    {
        foreach (var gate in _gates.Values)
        {
            gate.Dispose();
        }

        base.Dispose();
    }

    private async Task LoopAsync(string partner, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(jobs.CurrentValue.PollInterval, clock);
        try
        {
            do
            {
                try
                {
                    await RunPartnerOnceAsync(partner, null, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // Partner failures are recorded per partner order; this is the database or a bug. Keep the loop alive.
                    logger.LogError(ex, "Partner order run for {Partner} failed", partner);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task<PartnerOrderResult> CallPartnerAsync(IPartnerOrderGateway gateway, PartnerOrderWork item, CancellationToken cancellationToken)
    {
        try
        {
            return item.Poll && gateway.DecidesLater && item.Ticket is { } ticket
                ? await gateway.GetStatusAsync(ticket, cancellationToken)
                : await gateway.SubmitAsync(item.Request, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Gateways turn partner failures into RetryLater themselves; this is a bug, but the order must not get stuck.
            // The request may have gone out, so the outcome counts as unknown.
            logger.LogError(ex, "Order gateway {Partner} failed unexpectedly", item.Source);
            return new PartnerOrderResult(PartnerOrderOutcome.RetryLater, Error: $"Unexpected error calling {item.Source}: {ex.GetType().Name}.",
                OutcomeUnknown: !item.Poll);
        }
    }
}
