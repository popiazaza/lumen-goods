using Lumen.Api.Catalog;
using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Partners.Ports;
using Microsoft.EntityFrameworkCore;

namespace Lumen.Api.Partners.Sync;

public enum CatalogueRunResult
{
    NotModified,
    Applied,
    Failed,
}

/// <summary>What one catalogue run did (returned to the job and to tests).</summary>
public sealed record CatalogueOutcome(CatalogueRunResult Result, int Inserted = 0, int Updated = 0, int Unlisted = 0, int Unchanged = 0, string? Error = null)
{
    public bool Succeeded => Result != CatalogueRunResult.Failed;

    public int Written => Inserted + Updated + Unlisted;
}

public sealed record StockOutcome(bool Succeeded, int Updated = 0, int Unchanged = 0, string? Error = null);

/// <summary>
/// The partner-neutral sync engine: asks a partner's adapter for its catalogue or stock, then upserts the products by
/// (Source, SourceProductId), writing a row only when its content hash changed, and keeps PartnerSyncState and the
/// recent-errors list up to date.
/// BR-SYNC-01: a partner failure never breaks the shop. Every failure is caught and recorded, and the products stay as
/// they were (last good data). No database transaction is open while a partner is being called: the partner is read
/// first, then the result is written in one SaveChanges.
/// </summary>
internal sealed class PartnerSyncService(LumenDbContext db, SnapshotMapper mapper, TimeProvider clock, ILogger<PartnerSyncService> logger)
{
    /// <summary>Recent errors kept per partner (assumption A-54).</summary>
    public const int ErrorsKept = 50;

    /// <summary>Data problems recorded per run at most; the rest are summarised in one line.</summary>
    public const int ProblemsPerRun = 10;

    private const int MaxMessageLength = 2000;
    private const int IdLookupChunk = 1000;

    /// <summary>The partner's state row, created on first use.</summary>
    public async Task<PartnerSyncState> GetStateAsync(string partner, CancellationToken cancellationToken)
    {
        if (await db.PartnerSyncStates.FirstOrDefaultAsync(s => s.Partner == partner, cancellationToken) is { } state)
        {
            return state;
        }

        // Two writers (the job and the admin endpoint) may create it at once.
        await db.Database.ExecuteSqlAsync(
            $"""INSERT INTO "PartnerSyncStates" ("Partner", "LiveProductCount") VALUES ({partner}, 0) ON CONFLICT ("Partner") DO NOTHING""",
            cancellationToken);
        return await db.PartnerSyncStates.FirstAsync(s => s.Partner == partner, cancellationToken);
    }

    public async Task<CatalogueOutcome> SyncCatalogueAsync(PartnerRegistration partner, bool full, CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(partner.Code, cancellationToken);
        var startedUtc = clock.GetUtcNow();
        state.LastAttemptUtc = startedUtc;
        await db.SaveChangesAsync(cancellationToken);

        CatalogFetchResult fetched;
        try
        {
            fetched = await partner.Catalog.FetchCatalogAsync(new CatalogFetchRequest(full, state.Watermark), cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return await RecordCatalogueFailureAsync(partner, "catalogue", ex, cancellationToken);
        }

        if (fetched.Kind == CatalogFetchKind.NotModified)
        {
            state.LastSuccessUtc = clock.GetUtcNow();
            state.LastError = null;
            await db.SaveChangesAsync(cancellationToken);
            return new CatalogueOutcome(CatalogueRunResult.NotModified);
        }

        CatalogueOutcome outcome;
        List<string> problems;
        try
        {
            (outcome, problems) = await ApplyCatalogueAsync(partner, fetched, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            return await RecordCatalogueFailureAsync(partner, "catalogue", ex, cancellationToken);
        }

        // The products are saved; only now does the watermark move, so a crash in between just repeats the pass.
        var finishedUtc = clock.GetUtcNow();
        state.LastSuccessUtc = finishedUtc;
        state.LastError = null;
        state.Watermark = fetched.Watermark;
        if (fetched.Kind == CatalogFetchKind.Complete)
        {
            state.LastFullSyncUtc = finishedUtc;
        }

        state.LiveProductCount = await CountListedAsync(partner.Code, cancellationToken);
        await AddProblemsAsync(partner.Code, problems, finishedUtc, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await TrimErrorsAsync(partner.Code, cancellationToken);

        logger.Log(
            outcome.Written > 0 ? LogLevel.Information : LogLevel.Debug,
            "{Partner} catalogue {Kind}: {Inserted} new, {Updated} changed, {Unlisted} unlisted, {Unchanged} unchanged",
            partner.Code, fetched.Kind, outcome.Inserted, outcome.Updated, outcome.Unlisted, outcome.Unchanged);
        return outcome;
    }

    public async Task<StockOutcome> RefreshStockAsync(PartnerRegistration partner, CancellationToken cancellationToken)
    {
        var source = partner.Stock ?? throw new InvalidOperationException($"{partner.Code} has no separate stock source.");
        var references = await db.Products.AsNoTracking()
            .Where(p => p.Source == partner.Code && p.IsListed)
            .Select(p => p.SourceProductId)
            .ToListAsync(cancellationToken);

        StockFetchResult fetched;
        try
        {
            fetched = references.Count == 0
                ? new StockFetchResult(new Dictionary<string, StockLevel>(), [], [])
                : await source.FetchStockAsync(references, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var failure = await RecordFailureAsync(partner.Code, "stock", ex, setLastError: true, cancellationToken);
            return new StockOutcome(false, Error: failure);
        }

        var nowUtc = clock.GetUtcNow();
        var products = await LoadAsync(partner.Code, fetched.Levels.Keys, cancellationToken);
        int updated = 0, unchanged = 0;
        foreach (var (reference, level) in fetched.Levels)
        {
            if (!products.TryGetValue(reference, out var product))
            {
                continue;
            }

            var content = ProductContent.From(product) with { Quantity = level.Quantity, StatusWhenQuantityUnknown = level.StatusWhenQuantityUnknown };
            if (content.ComputeHash() == product.ContentHash)
            {
                unchanged++;
                continue;
            }

            content.ApplyTo(product);
            product.LastUpdatedUtc = nowUtc;
            updated++;
        }

        await db.SaveChangesAsync(cancellationToken);

        var state = await GetStateAsync(partner.Code, cancellationToken);
        string? error = null;
        if (fetched.Failures.Count == 0)
        {
            state.StockRefreshedUtc = nowUtc;
        }
        else
        {
            error = $"Stock refresh incomplete: {fetched.Failures.Count} request(s) failed; those products keep their last known stock. {fetched.Failures[0]}";
            state.LastError = Truncate(error);
            AddError(partner.Code, "stock", null, error, nowUtc);
        }

        await AddProblemsAsync(partner.Code, [.. fetched.Problems], nowUtc, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await TrimErrorsAsync(partner.Code, cancellationToken);
        return new StockOutcome(error is null, updated, unchanged, error);
    }

    /// <summary>Clears "Refresh now" if it is still the request this run served (a newer click is kept).</summary>
    public Task ClearRefreshRequestAsync(string partner, DateTimeOffset servedRequestUtc, CancellationToken cancellationToken) =>
        db.PartnerSyncStates
            .Where(s => s.Partner == partner && s.RefreshRequestedUtc == servedRequestUtc)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RefreshRequestedUtc, (DateTimeOffset?)null), cancellationToken);

    /// <summary>Marks a partner for an immediate full refresh; a pending request keeps its original time.</summary>
    public async Task<PartnerSyncState> RequestRefreshAsync(string partner, CancellationToken cancellationToken)
    {
        var nowUtc = clock.GetUtcNow();
        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO "PartnerSyncStates" ("Partner", "LiveProductCount", "RefreshRequestedUtc") VALUES ({partner}, 0, {nowUtc})
             ON CONFLICT ("Partner") DO UPDATE
             SET "RefreshRequestedUtc" = COALESCE("PartnerSyncStates"."RefreshRequestedUtc", EXCLUDED."RefreshRequestedUtc")
             """,
            cancellationToken);
        return await db.PartnerSyncStates.AsNoTracking().FirstAsync(s => s.Partner == partner, cancellationToken);
    }

    private async Task<(CatalogueOutcome Outcome, List<string> Problems)> ApplyCatalogueAsync(
        PartnerRegistration partner, CatalogFetchResult fetched, CancellationToken cancellationToken)
    {
        var settings = partner.Options();
        var complete = fetched.Kind == CatalogFetchKind.Complete;
        var existing = complete
            ? await db.Products.Where(p => p.Source == partner.Code).ToDictionaryAsync(p => p.SourceProductId, StringComparer.Ordinal, cancellationToken)
            : await LoadAsync(partner.Code, fetched.Products.Select(p => p.SourceProductId), cancellationToken);

        // Plan everything first: nothing is changed unless the whole catalogue passes the guard.
        var problems = new List<string>(fetched.Problems);
        var plans = new List<(PartnerProductSnapshot Snapshot, Product? Current, ProductContent Content, List<string> Problems)>();
        foreach (var snapshot in fetched.Products)
        {
            var current = existing.GetValueOrDefault(snapshot.SourceProductId);
            var itemProblems = new List<string>();
            if (mapper.ToContent(snapshot, current, itemProblems) is { } content)
            {
                plans.Add((snapshot, current, content, itemProblems));
            }
        }

        var dropped = new List<Product>();
        if (complete)
        {
            var inCatalogue = fetched.Products.Select(p => p.SourceProductId).ToHashSet(StringComparer.Ordinal);
            dropped = [.. existing.Values.Where(p => p.IsListed && !inCatalogue.Contains(p.SourceProductId))];
            var listedNow = existing.Values.Count(p => p.IsListed);
            var wouldUnlist = dropped.Count + plans.Count(p => p.Current?.IsListed == true && !p.Content.IsListed);
            if (MassDeletionGuard.Check(listedNow, wouldUnlist, fetched.Products.Count, settings.MaxUnlistFraction) is { } reason)
            {
                throw new PartnerDataException($"{settings.Name} catalogue not applied (mass-deletion guard): {reason}. The last good catalogue stays live.");
            }
        }

        var nowUtc = clock.GetUtcNow();
        int inserted = 0, updated = 0, unchanged = 0;
        foreach (var (snapshot, current, content, itemProblems) in plans)
        {
            if (current is not null && current.ContentHash == content.ComputeHash())
            {
                unchanged++;
                continue;
            }

            var changedAtUtc = ChangeTime(snapshot.UpdatedUtc ?? fetched.ChangedAtUtc, current?.LastUpdatedUtc, nowUtc);
            var product = current ?? new Product
            {
                Source = partner.Code,
                SourceProductId = snapshot.SourceProductId,
                Name = content.Name,
                Category = content.Category,
                FirstSeenUtc = nowUtc,
            };

            content.ApplyTo(product);
            product.LastUpdatedUtc = changedAtUtc;
            if (current is null)
            {
                db.Products.Add(product);
                inserted++;
            }
            else
            {
                updated++;
            }

            // Problems are recorded when the product is written, so an unchanged bad price is not re-reported every minute.
            problems.AddRange(itemProblems);
        }

        foreach (var product in dropped)
        {
            (ProductContent.From(product) with { IsListed = false }).ApplyTo(product);
            product.LastUpdatedUtc = nowUtc;
        }

        await db.SaveChangesAsync(cancellationToken);
        return (new CatalogueOutcome(CatalogueRunResult.Applied, inserted, updated, dropped.Count, unchanged), problems);
    }

    /// <summary>
    /// The partner's own change time when it says one and it is newer than what is stored (Harbor updated_at, Kestrel
    /// generated); otherwise now (a Lumen-side change, such as a new FX rate, or a partner clock in the future).
    /// </summary>
    private static DateTimeOffset ChangeTime(DateTimeOffset? partnerTimeUtc, DateTimeOffset? storedUtc, DateTimeOffset nowUtc) =>
        partnerTimeUtc is { } partnerTime && partnerTime <= nowUtc && (storedUtc is null || partnerTime > storedUtc)
            ? partnerTime.ToUniversalTime()
            : nowUtc;

    private async Task<Dictionary<string, Product>> LoadAsync(string partner, IEnumerable<string> references, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, Product>(StringComparer.Ordinal);
        foreach (var chunk in references.Distinct(StringComparer.Ordinal).Chunk(IdLookupChunk))
        {
            var ids = chunk.ToList();
            foreach (var product in await db.Products.Where(p => p.Source == partner && ids.Contains(p.SourceProductId)).ToListAsync(cancellationToken))
            {
                result[product.SourceProductId] = product;
            }
        }

        return result;
    }

    private Task<int> CountListedAsync(string partner, CancellationToken cancellationToken) =>
        db.Products.CountAsync(p => p.Source == partner && p.IsListed, cancellationToken);

    private async Task<CatalogueOutcome> RecordCatalogueFailureAsync(
        PartnerRegistration partner, string operation, Exception ex, CancellationToken cancellationToken)
    {
        var message = await RecordFailureAsync(partner.Code, operation, ex, setLastError: true, cancellationToken);
        return new CatalogueOutcome(CatalogueRunResult.Failed, Error: message);
    }

    private async Task<string> RecordFailureAsync(string partner, string operation, Exception ex, bool setLastError, CancellationToken cancellationToken)
    {
        var (status, message) = ex switch
        {
            PartnerException partnerFailure => (partnerFailure.StatusCode, partnerFailure.Message),
            _ => ((int?)null, $"Unexpected error during {operation} sync: {ex.GetType().Name}: {ex.Message}"),
        };

        if (ex is PartnerException)
        {
            logger.LogWarning("{Partner} {Operation} failed: {Message}", partner, operation, message);
        }
        else
        {
            logger.LogError(ex, "{Partner} {Operation} failed unexpectedly", partner, operation);
        }

        var nowUtc = clock.GetUtcNow();
        var state = await GetStateAsync(partner, cancellationToken);
        if (setLastError)
        {
            state.LastError = Truncate(message);
        }

        AddError(partner, operation, status, message, nowUtc);
        await db.SaveChangesAsync(cancellationToken);
        await TrimErrorsAsync(partner, cancellationToken);
        return message;
    }

    private void AddError(string partner, string operation, int? status, string message, DateTimeOffset occurredUtc) =>
        db.PartnerSyncErrors.Add(new PartnerSyncError
        {
            Partner = partner,
            Operation = operation,
            StatusCode = status,
            Message = Truncate(message),
            OccurredUtc = occurredUtc,
        });

    /// <summary>
    /// Records data problems (bad prices, skipped items...) under operation "data". A problem already in the recent
    /// list is not repeated, and at most <see cref="ProblemsPerRun"/> are added per run, so data noise cannot push
    /// real failures out of the list.
    /// </summary>
    private async Task AddProblemsAsync(string partner, List<string> problems, DateTimeOffset occurredUtc, CancellationToken cancellationToken)
    {
        if (problems.Count == 0)
        {
            return;
        }

        var known = (await db.PartnerSyncErrors.AsNoTracking()
                .Where(e => e.Partner == partner)
                .Select(e => e.Message)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var fresh = problems.Select(Truncate).Distinct(StringComparer.Ordinal).Where(p => !known.Contains(p)).ToList();
        foreach (var problem in fresh.Take(ProblemsPerRun))
        {
            AddError(partner, "data", null, problem, occurredUtc);
        }

        if (fresh.Count > ProblemsPerRun)
        {
            AddError(partner, "data", null, $"{fresh.Count - ProblemsPerRun} more data problems in this run (see the logs).", occurredUtc);
            foreach (var problem in fresh.Skip(ProblemsPerRun))
            {
                logger.LogWarning("{Partner} data problem: {Problem}", partner, problem);
            }
        }
    }

    private async Task TrimErrorsAsync(string partner, CancellationToken cancellationToken)
    {
        var stale = db.PartnerSyncErrors
            .Where(e => e.Partner == partner)
            .OrderByDescending(e => e.OccurredUtc)
            .ThenByDescending(e => e.Id)
            .Skip(ErrorsKept)
            .Select(e => e.Id);
        await db.PartnerSyncErrors.Where(e => stale.Contains(e.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    private static string Truncate(string text) => text.Length <= MaxMessageLength ? text : text[..(MaxMessageLength - 3)] + "...";
}
