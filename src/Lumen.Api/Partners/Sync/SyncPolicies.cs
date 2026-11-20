using System.Globalization;

namespace Lumen.Api.Partners.Sync;

/// <summary>
/// BR-SYNC-02 (last good data kept): when a full catalogue leaves out many listed products at once, those removals
/// are held back (the products stay listed) while everything else in the catalogue is still applied. They are held
/// when the catalogue is empty, or when they are at least <c>minRemovals</c> products AND more than
/// <c>maxRemovalFraction</c> of the listed ones. A partner losing most of its catalogue at once is far more likely a
/// broken feed than a business decision; small catalogues still shrink normally. An admin can confirm the removals
/// ("Refresh now" with confirmRemovals). Products the partner marks inactive or moves to an unmapped category are
/// explicit changes, not removals, and are always applied.
/// </summary>
public static class MassDeletionGuard
{
    /// <param name="removals">Listed products missing from the catalogue.</param>
    /// <returns>Why the removals are held back, or null when they may be applied.</returns>
    public static string? Check(int listedNow, int removals, int productsInCatalogue, double maxRemovalFraction, int minRemovals)
    {
        if (listedNow == 0 || removals == 0)
        {
            return null;
        }

        if (productsInCatalogue == 0)
        {
            return $"the catalogue is empty while {listedNow} products are listed";
        }

        return removals >= minRemovals && (double)removals / listedNow > maxRemovalFraction
            ? string.Create(CultureInfo.InvariantCulture,
                $"{removals} of the {listedNow} listed products are missing (limit: {maxRemovalFraction:0.##} of them once at least {minRemovals} are missing)")
            : null;
    }
}

/// <summary>
/// BR-SYNC-01 (freshness): when each partner job does what. The configured intervals keep every partner change visible
/// within 15 minutes, counting the job's wake-up interval and the run itself (a unit test checks the shipped values):
/// Harbor incremental every 60 s plus a full pass every 10 min (the only way to see removals); Kestrel feed every
/// 3 min with an unconditional fetch every 9 min (a stuck Last-Modified cannot hide changes), stock every 5 min.
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
