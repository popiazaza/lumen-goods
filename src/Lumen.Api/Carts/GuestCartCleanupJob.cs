using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Partners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Carts;

/// <summary>
/// Deletes guest carts nobody has touched for <see cref="CartOptions.GuestCartLifetime"/> (BR-CART-01, A-48): their
/// cookie has expired, so no browser can reach them any more. Customers' saved carts are never deleted. Runs every
/// <see cref="CartOptions.GuestCartCleanupInterval"/> unless background jobs are off (<c>Jobs:Enabled</c>); tests call
/// <see cref="RunOnceAsync"/> directly.
/// </summary>
internal sealed class GuestCartCleanupJob(
    IServiceScopeFactory scopes,
    IOptions<CartOptions> options,
    IOptionsMonitor<JobsOptions> jobs,
    TimeProvider clock,
    ILogger<GuestCartCleanupJob> logger) : BackgroundService
{
    /// <summary>Deleted per statement, so a large backlog never holds one long transaction.</summary>
    private const int BatchSize = 1000;

    /// <returns>How many guest carts were deleted.</returns>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LumenDbContext>();
        var expiredAtOrBefore = clock.GetUtcNow() - options.Value.GuestCartLifetime;
        var total = 0;
        int deleted;
        do
        {
            // Lines go with their cart (ON DELETE CASCADE). Uses the partial index on guest carts' UpdatedUtc.
            var batch = db.Carts
                .Where(c => c.CustomerId == null && c.UpdatedUtc <= expiredAtOrBefore)
                .OrderBy(c => c.UpdatedUtc)
                .Take(BatchSize)
                .Select(c => c.Id);
            deleted = await db.Carts.Where(c => batch.Contains(c.Id)).ExecuteDeleteAsync(cancellationToken);
            total += deleted;
        }
        while (deleted == BatchSize);

        if (total > 0)
        {
            logger.LogInformation("Deleted {Count} guest carts untouched since {Cutoff:u}", total, expiredAtOrBefore);
        }

        return total;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!jobs.CurrentValue.Enabled)
        {
            logger.LogInformation("Guest cart clean-up is disabled (Jobs:Enabled=false)");
            return;
        }

        await Task.Yield();
        using var timer = new PeriodicTimer(options.Value.GuestCartCleanupInterval, clock);
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
                    logger.LogError(ex, "Guest cart clean-up failed; retrying at the next interval");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
