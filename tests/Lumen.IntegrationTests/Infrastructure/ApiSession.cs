using System.Net.Http.Json;

namespace Lumen.IntegrationTests.Infrastructure;

/// <summary>
/// Behaves like the Angular client: keeps cookies (via the factory's client) and echoes the XSRF-TOKEN cookie
/// in the X-XSRF-TOKEN header on state-changing requests.
/// </summary>
public sealed class ApiSession(HttpClient client)
{
    private string? _xsrfToken;

    public HttpClient Client => client;

    /// <summary>The XSRF token most recently issued to this session.</summary>
    public string? XsrfToken => _xsrfToken;

    public async Task<HttpResponseMessage> GetAsync(string url)
    {
        var response = await client.GetAsync(url);
        CaptureXsrfToken(response);
        return response;
    }

    /// <param name="xsrfTokenOverride">Send this token instead of the latest one (to test stale tokens).</param>
    public async Task<HttpResponseMessage> PostAsJsonAsync<T>(string url, T body, bool withXsrfHeader = true, string? xsrfTokenOverride = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        var token = xsrfTokenOverride ?? _xsrfToken;
        if (withXsrfHeader && token is not null)
        {
            request.Headers.Add("X-XSRF-TOKEN", token);
        }

        var response = await client.SendAsync(request);
        CaptureXsrfToken(response);
        return response;
    }

    /// <summary>Starts a session the way the SPA does on load, which also issues the XSRF token.</summary>
    public async Task StartAsync() => (await GetAsync("/api/v1/auth/session")).EnsureSuccessStatusCode();

    private void CaptureXsrfToken(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return;
        }

        foreach (var cookie in cookies)
        {
            if (cookie.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal))
            {
                var value = cookie["XSRF-TOKEN=".Length..].Split(';', 2)[0];
                _xsrfToken = Uri.UnescapeDataString(value);
            }
        }
    }
}
