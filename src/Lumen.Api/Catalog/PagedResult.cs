using System.Globalization;

namespace Lumen.Api.Catalog;

/// <summary>One page of a list. <see cref="Page"/> is 1-based; a page past the end has no items but keeps the totals.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount, int TotalPages)
{
    public static PagedResult<T> Create(IReadOnlyList<T> items, PageRequest paging, int totalCount) =>
        new(items, paging.Page, paging.PageSize, totalCount,
            totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)paging.PageSize));
}

/// <summary>A validated <c>page</c>/<c>pageSize</c> pair (offset paging; see <see cref="Skip"/>).</summary>
public readonly record struct PageRequest(int Page, int PageSize)
{
    public const int DefaultPageSize = 24;
    public const int MaxPageSize = 96;

    public static readonly PageRequest First = new(1, DefaultPageSize);

    public int Skip => (Page - 1) * PageSize;

    /// <summary>Parses the raw query values; problems are added to <paramref name="errors"/> keyed by parameter name.</summary>
    public static PageRequest Parse(string? page, string? pageSize, IDictionary<string, string[]> errors)
    {
        var pageNumber = ParseInt(page, fallback: 1, max: int.MaxValue);
        var size = ParseInt(pageSize, fallback: DefaultPageSize, max: MaxPageSize);
        if (pageNumber is null)
        {
            errors["page"] = ["page must be a whole number of at least 1."];
        }

        if (size is null)
        {
            errors["pageSize"] = [$"pageSize must be a whole number from 1 to {MaxPageSize}."];
        }

        var result = new PageRequest(pageNumber ?? 1, size ?? DefaultPageSize);
        if ((long)(result.Page - 1) * result.PageSize > int.MaxValue)
        {
            errors["page"] = ["page is too large."];
            return First;
        }

        return result;

        static int? ParseInt(string? value, int fallback, int max)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            return int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 && number <= max
                ? number
                : null;
        }
    }
}

/// <summary>Builds LIKE/ILIKE patterns that match user text literally.</summary>
public static class LikePattern
{
    /// <summary>The escape character passed to ILIKE together with <see cref="Contains"/> patterns.</summary>
    public const string EscapeCharacter = "\\";

    /// <summary>
    /// "%term%" with the LIKE wildcards in <paramref name="term"/> escaped, so a search for "100%" or "a_b" matches
    /// those characters instead of acting as a wildcard (EC-BR-04). The value is still sent as a query parameter.
    /// </summary>
    public static string Contains(string term) =>
        "%" + term.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";
}
