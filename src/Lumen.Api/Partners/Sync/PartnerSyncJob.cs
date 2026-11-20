using Lumen.Api.Partners.Mapping;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Partners.Sync;

/// <summary>What one wake-up of a partner job did; null parts did not run.</summary>
public sealed record PartnerRunReport(CatalogueOutcome? Catalogue, StockOutcome? Stock)
{
    public static readonly PartnerRunReport Nothing = new(null, null);
}

/// <summary>
/// One background job per partner: a thin loop that wakes every <see cref="JobsOptions.PollInterval"/> and calls
/// <see cref="RunOnceAsync"/>, which does whatever is due (<see cref="SyncSchedule"/>) or what "Refresh now" asked for
/// (Harbor: full pass; Kestrel: unconditional feed + all stock). Tests switch the loop off (Jobs:Enabled=false) and
/// call RunOnceAsync directly.
/// </summary>
internal sealed class PartnerSyncJob(
    PartnerRegistration partner,
    IServiceScopeFactory scopes,
    CurrencyConverter currency,
    IOptionsMonitor<PartnersOptions> partners,
    IOptionsMonitor<JobsOptions> jobs,
    TimeProvider clock,
    ILogger<PartnerSyncJob> logger) : BackgroundService
{
    private readonly SemaphoreSlim _running = new(1, 1);

    // In memory on purpose: after a restart the first run is a full pass (re-mapping everything with the current
    // configuration), and the stock attempt time only stops a failing stock call from repeating every few seconds.
    private string? _appliedMappingFingerprint;
    private DateTimeOffset? _lastCatalogueAttemptUtc;
    private DateTimeOffset? _lastStockAttemptUtc;

    public string Partner => partner.Code;

    public async Task<PartnerRunReport> RunOnceAsync(CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken);
        try
        {
            var options = partner.Options();
            if (!options.IsConfigured)
            {
                return PartnerRunReport.Nothing;
            }

            await using var scope = scopes.CreateAsyncScope();
            var sync = scope.ServiceProvider.GetRequiredService<PartnerSyncService>();
            var state = await sync.GetStateAsync(partner.Code, cancellationToken);
            var refreshRequestedUtc = state.RefreshRequestedUtc;
            var removalsConfirmedUtc = state.RemovalsConfirmedUtc;
            var nowUtc = clock.GetUtcNow();
            var fingerprint = MappingFingerprint(options);

            CatalogueOutcome? catalogue = null;
            var (runCatalogue, full) = SyncSchedule.Catalogue(state, nowUtc, options, fingerprint != _appliedMappingFingerprint, _lastCatalogueAttemptUtc);
            if (runCatalogue)
            {
                _lastCatalogueAttemptUtc = nowUtc;
                catalogue = await sync.SyncCatalogueAsync(partner, full, cancellationToken, removalsConfirmed: full && removalsConfirmedUtc is not null);
                if (full && catalogue.Succeeded)
                {
                    _appliedMappingFingerprint = fingerprint;
                }
            }

            StockOutcome? stock = null;
            var catalogueApplied = catalogue?.Result == CatalogueRunResult.Applied;
            if (partner.Stock is not null && SyncSchedule.Stock(state, nowUtc, options, catalogueApplied, _lastStockAttemptUtc))
            {
                _lastStockAttemptUtc = nowUtc;
                stock = await sync.RefreshStockAsync(partner, cancellationToken);
            }

            // Cleared whatever the outcome: the outcome is on the admin page, and a failing partner must not be retried
            // every few seconds. A removal confirmation is spent only by a full pass, the one that can apply it.
            var servedConfirmation = runCatalogue && full ? removalsConfirmedUtc : null;
            if (refreshRequestedUtc is not null || servedConfirmation is not null)
            {
                await sync.ClearRefreshRequestAsync(partner.Code, refreshRequestedUtc, servedConfirmation, cancellationToken);
            }

            return new PartnerRunReport(catalogue, stock);
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
            logger.LogInformation("{Partner} sync job is disabled (Jobs:Enabled=false)", partner.Code);
            return;
        }

        if (!partner.Options().IsConfigured)
        {
            logger.LogWarning("{Partner} sync job not started: Partners:{Partner}:BaseUrl is not configured", partner.Code, partner.Code);
            return;
        }

        // Let the host finish starting before the first (possibly slow) partner call.
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
                    // Partner failures are recorded inside; this is the database or a bug. Keep the loop alive.
                    logger.LogError(ex, "{Partner} sync run failed", partner.Code);
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

    /// <summary>Everything outside the partner's data that shapes stored products: FX rate, category map, synonyms.</summary>
    private string MappingFingerprint(PartnerOptions options) =>
        string.Join("\n",
            currency.Fingerprint(),
            new CategoryMap(options.CategoryMap).Fingerprint(),
            string.Join(";", partners.CurrentValue.EffectiveSynonyms().OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key}={kv.Value}")));
}
