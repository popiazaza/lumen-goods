namespace Lumen.Api.Partners;

/// <summary>URL rules for partner calls: where requests go, and which URLs may receive partner credentials.</summary>
public static class PartnerUrls
{
    /// <summary>An absolute http(s) base URL with a trailing slash, or null when empty or invalid.</summary>
    public static Uri? ParseBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        var text = baseUrl.Trim();
        if (!text.EndsWith('/'))
        {
            text += "/";
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && IsHttp(uri) && string.IsNullOrEmpty(uri.Query) ? uri : null;
    }

    /// <summary>
    /// Same scheme, host and port. Partner credentials and image fetches are allowed only for the configured partner
    /// origin (SSRF guard), so a URL inside partner data can never make Lumen call, or send a key to, another host.
    /// </summary>
    public static bool IsSameOrigin(Uri candidate, Uri origin) =>
        candidate.IsAbsoluteUri && origin.IsAbsoluteUri
        && Uri.Compare(candidate, origin, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>
    /// A partner link as an absolute URL: absolute http(s) links as they are, anything else taken as relative to the
    /// partner's base URL (Harbor: "media[].href is relative to the Harbor service's base URL"). Null when unusable.
    /// </summary>
    public static Uri? Resolve(Uri baseUri, string? link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return null;
        }

        var text = link.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && absolute.Scheme is not "file")
        {
            return IsHttp(absolute) ? absolute : null;
        }

        return Uri.TryCreate(baseUri, text.TrimStart('/'), out var combined) && IsHttp(combined) ? combined : null;
    }

    private static bool IsHttp(Uri uri) => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
}
