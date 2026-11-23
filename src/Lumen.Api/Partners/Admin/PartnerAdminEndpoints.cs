using Lumen.Api.Identity;
using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Partners.Sync;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Lumen.Api.Partners.Admin;

/// <summary>One partner on the admin "Partner status" page.</summary>
/// <param name="LiveProductCount">Products of this partner currently shown in the shop.</param>
/// <param name="RefreshRequestedUtc">Set while a "Refresh now" is waiting or running.</param>
/// <param name="HeldRemovals">
/// Products missing from the partner's last full catalogue that the mass-deletion guard keeps listed until an admin
/// confirms them (refresh with confirmRemovals=true). 0 when nothing is held.
/// </param>
public sealed record PartnerStatusResponse(
    string Partner,
    string Name,
    DateTime? LastSuccessUtc,
    DateTime? LastAttemptUtc,
    string? LastError,
    int LiveProductCount,
    DateTime? RefreshRequestedUtc,
    int HeldRemovals,
    IReadOnlyList<PartnerErrorResponse> RecentErrors);

/// <param name="Operation">catalogue, stock, data or order.</param>
public sealed record PartnerErrorResponse(DateTime OccurredUtc, string Operation, int? StatusCode, string Message);

public sealed record PartnerRefreshResponse(string Partner, DateTime? RefreshRequestedUtc, bool RemovalsConfirmed);

internal static class PartnerAdminEndpoints
{
    public const int RecentErrorsShown = 10;

    public static RouteGroupBuilder MapPartnerAdmin(this RouteGroupBuilder api)
    {
        var partners = api.MapGroup("/admin/partners")
            .WithTags("Admin: partners")
            .RequireAuthorization(Policies.Admin);

        partners.MapGet("", ListPartners).WithSummary("Catalogue sync status of every partner");
        partners.MapPost("/{partner}/refresh", RequestRefresh)
            .WithSummary("Refresh a partner's catalogue now (runs in the background)")
            .WithDescription(
                "Runs a full catalogue pass within a few seconds (Harbor: every page; Kestrel: unconditional feed plus all stock). "
                + "With ?confirmRemovals=true that pass also removes the products the mass-deletion guard is holding back "
                + "(heldRemovals on the partner status), after an admin has checked they really left the partner's catalogue.");
        return api;
    }

    private static async Task<Ok<List<PartnerStatusResponse>>> ListPartners(
        PartnerRegistry registry, LumenDbContext db, CancellationToken cancellationToken)
    {
        var codes = registry.All.Select(p => p.Code).ToList();
        var states = await db.PartnerSyncStates.AsNoTracking()
            .Where(s => codes.Contains(s.Partner))
            .ToDictionaryAsync(s => s.Partner, cancellationToken);

        var result = new List<PartnerStatusResponse>();
        foreach (var partner in registry.All)
        {
            var errors = await db.PartnerSyncErrors.AsNoTracking()
                .Where(e => e.Partner == partner.Code)
                .OrderByDescending(e => e.OccurredUtc)
                .ThenByDescending(e => e.Id)
                .Take(RecentErrorsShown)
                .Select(e => new { e.OccurredUtc, e.Operation, e.StatusCode, e.Message })
                .ToListAsync(cancellationToken);

            var state = states.GetValueOrDefault(partner.Code);
            result.Add(new PartnerStatusResponse(
                Partner: partner.Code,
                Name: partner.Options().Name,
                LastSuccessUtc: state?.LastSuccessUtc?.UtcDateTime,
                LastAttemptUtc: state?.LastAttemptUtc?.UtcDateTime,
                LastError: state?.LastError,
                LiveProductCount: state?.LiveProductCount ?? 0,
                RefreshRequestedUtc: state?.RefreshRequestedUtc?.UtcDateTime,
                HeldRemovals: state?.HeldRemovals ?? 0,
                RecentErrors: [.. errors.Select(e => new PartnerErrorResponse(e.OccurredUtc.UtcDateTime, e.Operation, e.StatusCode, e.Message))]));
        }

        return TypedResults.Ok(result);
    }

    private static async Task<Results<Accepted<PartnerRefreshResponse>, ProblemHttpResult>> RequestRefresh(
        string partner, PartnerRegistry registry, PartnerSyncService sync, CancellationToken cancellationToken, bool confirmRemovals = false)
    {
        if (registry.Find(partner) is not { } registration)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: $"Unknown partner '{partner}'");
        }

        // The partner's job picks the request up within a few seconds (Jobs:PollInterval).
        var state = await sync.RequestRefreshAsync(registration.Code, confirmRemovals, cancellationToken);
        return TypedResults.Accepted(
            "/api/v1/admin/partners",
            new PartnerRefreshResponse(registration.Code, state.RefreshRequestedUtc?.UtcDateTime, state.RemovalsConfirmedUtc is not null));
    }
}
