using Microsoft.Extensions.Options;

namespace KestrelOutfitters.StandIn.Orders;

/// <summary>
/// Decides orders when their time comes even if nobody asks, so Kestrel's stock moves on its own
/// (Kestrel never calls Lumen back; Lumen only sees the outcome by polling).
/// </summary>
internal sealed class OrderDecisionService(
    OrderDecider decider,
    TimeProvider time,
    IOptions<KestrelOutfittersOptions> options,
    ILogger<OrderDecisionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.OrderDecisionPollInterval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await decider.DecideDueOrdersAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A locked database (for example during a CLI change) must not stop future decisions.
                logger.LogWarning(ex, "Deciding due orders failed; retrying on the next tick");
            }
        }
    }
}
