using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace HarborSupply.StandIn.Catalogue;

/// <summary>The validated query string of GET /v2/catalog/items. Anything invalid is a 400, never silently clamped.</summary>
internal sealed record CatalogueQuery(int Limit, PageCursor? After, DateTime? UpdatedSince)
{
    public const int MaxLimit = 25;

    // ISO 8601 with an explicit zone ("Z" or "+hh:mm"), optional fractional seconds. A timestamp without a
    // zone is rejected rather than guessed, because the contract says the value is UTC.
    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
    ];

    /// <remarks>An empty value (<c>?cursor=</c>) is treated as not sent.</remarks>
    public static bool TryParse(
        string? limit, string? cursor, string? updatedSince,
        [NotNullWhen(true)] out CatalogueQuery? query, [NotNullWhen(false)] out string? error)
    {
        query = null;
        var pageSize = MaxLimit;
        if (!string.IsNullOrEmpty(limit)
            && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize is < 1 or > MaxLimit))
        {
            error = $"limit must be an integer from 1 to {MaxLimit}.";
            return false;
        }

        PageCursor? after = null;
        if (!string.IsNullOrEmpty(cursor) && !PageCursor.TryDecode(cursor, out after))
        {
            error = "cursor is not a value returned in next_cursor.";
            return false;
        }

        DateTime? since = null;
        if (!string.IsNullOrEmpty(updatedSince))
        {
            if (!DateTimeOffset.TryParseExact(updatedSince, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                error = "updated_since must be an ISO 8601 UTC timestamp such as 2026-10-02T09:15:00Z.";
                return false;
            }

            since = parsed.UtcDateTime;
        }

        query = new CatalogueQuery(pageSize, after, since);
        error = null;
        return true;
    }
}
