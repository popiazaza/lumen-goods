using System.Globalization;

namespace Lumen.Api.Partners.Sync;

/// <summary>
/// BR-SYNC-01 (last good data kept): a full catalogue that is empty, or that would take more than
/// <c>maxUnlistFraction</c> of the currently listed products out of the shop (dropped, inactive or unmapped), is not
/// applied. A partner losing most of its catalogue at once is far more likely a broken feed than a business decision;
/// the admin page shows the rejection, and the threshold is configuration.
/// </summary>
public static class MassDeletionGuard
{
    /// <returns>Why the catalogue is rejected, or null when it may be applied.</returns>
    public static string? Check(int listedNow, int wouldUnlist, int productsInCatalogue, double maxUnlistFraction)
    {
        if (listedNow == 0)
        {
            return null;
        }

        if (productsInCatalogue == 0)
        {
            return $"the catalogue is empty while {listedNow} products are listed";
        }

        return (double)wouldUnlist / listedNow > maxUnlistFraction
            ? string.Create(CultureInfo.InvariantCulture,
                $"it would take {wouldUnlist} of the {listedNow} listed products out of the shop (limit {maxUnlistFraction:0.##} of them)")
            : null;
    }
}

/// <summary>
/// BR-SYNC-01 (freshness): when each partner job does what. The intervals (configuration) keep every partner change
/// visible within 15 minutes: Harbor incremental every 60 s plus a full pass every 15 min (the only way to see
/// removals); Kestrel feed every 3 min and stock every 5 min.
/// </summary>
public static class SyncSchedule
{
    /// <param name="lastAttemptThisProcessUtc">The last catalogue attempt made by this process (null after a restart).</param>
    /// <param name="mappingChanged">The FX rate, category map or synonyms differ from the last applied full pass.</param>
    public static (bool Run, bool Full) Catalogue(
        PartnerSyncState state, DateTimeOffset now, PartnerOptions options, bool mappingChanged, DateTimeOffset? lastAttemptThisProcessUtc)
    {
        var refresh = state.RefreshRequestedUtc is not null;

        // A mapping change re-maps unchanged products with a full pass, at once after a restart, otherwise at most
        // once per interval so a failing partner is not hammered.
        var mappingDue = mappingChanged && (lastAttemptThisProcessUtc is null || now - lastAttemptThisProcessUtc >= options.CatalogueInterval);
        var due = refresh || mappingDue || state.LastAttemptUtc is null || now - state.LastAttemptUtc >= options.CatalogueInterval;
        if (!due)
        {
            return (false, false);
        }

        var full = refresh || mappingChanged || state.Watermark is null || state.LastFullSyncUtc is null
            || now - state.LastFullSyncUtc >= options.FullSyncInterval;
        return (true, full);
    }

    /// <param name="catalogueApplied">The catalogue run just stored a new partner catalogue (not a 304, not a failure).</param>
    public static bool Stock(
        PartnerSyncState state, DateTimeOffset now, PartnerOptions options, bool catalogueApplied, DateTimeOffset? lastStockAttemptUtc)
    {
        if (state.RefreshRequestedUtc is not null || catalogueApplied)
        {
            return true;
        }

        var last = Max(state.StockRefreshedUtc, lastStockAttemptUtc);
        return last is null || now - last >= options.StockInterval;
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;
}
