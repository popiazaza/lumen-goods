using Lumen.Api.Identity;
using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Partners.Sync;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Lumen.Api.Partners.Admin;

/// <summary>One partner on the admin "Partner status" page.</summary>
/// <param name="LiveProductCount">Products of this partner currently shown in the shop.</param>
/// <param name="RefreshRequestedUtc">Set while a "Refresh now" is waiting or running.</param>
public sealed record PartnerStatusResponse(
    string Partner,
    string Name,
    DateTime? LastSuccessUtc,
    DateTime? LastAttemptUtc,
    string? LastError,
    int LiveProductCount,
    DateTime? RefreshRequestedUtc,
    IReadOnlyList<PartnerErrorResponse> RecentErrors);

/// <param name="Operation">catalogue, stock or data.</param>
public sealed record PartnerErrorResponse(DateTime OccurredUtc, string Operation, int? StatusCode, string Message);

public sealed record PartnerRefreshResponse(string Partner, DateTime? RefreshRequestedUtc);

internal static class PartnerAdminEndpoints
{
    public const int RecentErrorsShown = 10;

    public static RouteGroupBuilder MapPartnerAdmin(this RouteGroupBuilder api)
    {
        var partners = api.MapGroup("/admin/partners")
            .WithTags("Admin: partners")
            .RequireAuthorization(Policies.Admin);

        partners.MapGet("", ListPartners).WithSummary("Catalogue sync status of every partner");
        partners.MapPost("/{partner}/refresh", RequestRefresh).WithSummary("Refresh a partner's catalogue now (runs in the background)");
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
                RecentErrors: [.. errors.Select(e => new PartnerErrorResponse(e.OccurredUtc.UtcDateTime, e.Operation, e.StatusCode, e.Message))]));
        }

        return TypedResults.Ok(result);
    }

    private static async Task<Results<Accepted<PartnerRefreshResponse>, ProblemHttpResult>> RequestRefresh(
        string partner, PartnerRegistry registry, PartnerSyncService sync, CancellationToken cancellationToken)
    {
        if (registry.Find(partner) is not { } registration)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: $"Unknown partner '{partner}'");
        }

        // The partner's job picks the request up within a few seconds (Jobs:PollInterval).
        var state = await sync.RequestRefreshAsync(registration.Code, cancellationToken);
        return TypedResults.Accepted("/api/v1/admin/partners", new PartnerRefreshResponse(registration.Code, state.RefreshRequestedUtc?.UtcDateTime));
    }
}
