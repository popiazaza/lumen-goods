using Lumen.Api.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Partners.Media;

/// <summary>
/// Fills the image cache before browsers ask: after a successful catalogue sync, fetches the primary image of the first
/// <see cref="MediaOptions.WarmImageCount"/> listed products of the partner (newest first, the catalogue's default order)
/// that are not cached yet, one at a time. Each fetch takes only a spare rate-limit token (Harbor: from a full image
/// bucket) and never queues, so a browser always finds the burst less one.
/// </summary>
internal sealed class PartnerImageWarmer(
    IServiceScopeFactory scopes,
    PartnerImageProxy proxy,
    IOptionsMonitor<MediaOptions> options,
    TimeProvider time,
    ILogger<PartnerImageWarmer> logger)
{
    public async Task WarmAsync(string source, CancellationToken cancellationToken)
    {
        try
        {
            var fetched = 0;
            foreach (var url in await PrimaryImageUrlsAsync(source, options.CurrentValue.WarmImageCount, cancellationToken))
            {
                ImageResult? result;
                while ((result = await proxy.WarmAsync(source, url, cancellationToken)) is { Outcome: ImageOutcome.Throttled })
                {
                    await Task.Delay(result.RetryAfter ?? TimeSpan.FromSeconds(1), time, cancellationToken);
                }

                fetched += result?.Outcome == ImageOutcome.Found ? 1 : 0;
            }

            if (fetched > 0)
            {
                logger.LogInformation("{Partner}: {Count} product images fetched into the cache ahead of browsers", source, fetched);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // Only an optimisation: browsers still get every image on demand.
            logger.LogWarning(ex, "{Partner} image warm-up stopped", source);
        }
    }

    private async Task<List<string>> PrimaryImageUrlsAsync(string source, int count, CancellationToken cancellationToken)
    {
        if (count == 0)
        {
            return [];
        }

        await using var scope = scopes.CreateAsyncScope();
        var images = await scope.ServiceProvider.GetRequiredService<LumenDbContext>().Products.AsNoTracking()
            .Where(p => p.Source == source && p.IsListed)
            .OrderByDescending(p => p.FirstSeenUtc).ThenByDescending(p => p.Id)
            .Take(count)
            .Select(p => p.Images)
            .ToListAsync(cancellationToken);

        // The image a catalogue card shows: the primary one, else the first.
        return [.. images.Select(i => (i.FirstOrDefault(image => image.IsPrimary) ?? i.FirstOrDefault())?.SourceUrl).OfType<string>()];
    }
}
