using System.Net;
using System.Net.Http.Json;
using Lumen.Api.Identity;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lumen.IntegrationTests.Identity;

/// <summary>Sessions and XSRF tokens outlive the API process when the keys are kept in a directory (a volume in compose).</summary>
public sealed class DataProtectionKeysTests(LumenApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task With_a_keys_directory_a_session_and_its_xsrf_token_survive_an_api_restart()
    {
        var keys = Directory.CreateTempSubdirectory("lumen-keys-");
        var email = $"restart-{Guid.NewGuid():N}@lumengoods.test";
        try
        {
            var browser = new CookieJar();
            await using (var before = Started(keys))
            {
                var client = before.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
                await browser.SendAsync(client, HttpMethod.Get, "/api/v1/auth/session");
                (await browser.SendAsync(client, HttpMethod.Post, "/api/v1/auth/register", new RegisterRequest(email, "Shopper!2026")))
                    .EnsureSuccessStatusCode();
            }

            keys.GetFiles("key-*.xml").Should().NotBeEmpty("the keys are kept in the configured directory");

            await using var after = Started(keys);
            var newClient = after.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            var post = await browser.SendAsync(newClient, HttpMethod.Post, "/api/v1/cart/acknowledge", new { });
            var session = await browser.SendAsync(newClient, HttpMethod.Get, "/api/v1/auth/session");

            post.StatusCode.Should().Be(HttpStatusCode.OK, "the XSRF token issued before the restart is still valid");
            (await session.Content.ReadFromJsonAsync<SessionResponse>(Ct))!.Email.Should().Be(email);
        }
        finally
        {
            keys.Delete(recursive: true);
        }
    }

    private WebApplicationFactory<Program> Started(DirectoryInfo keys) =>
        factory.WithWebHostBuilder(builder => builder.UseSetting("DataProtection:KeysPath", keys.FullName));

    /// <summary>A browser's cookies, kept outside any one test server so they can outlive it; echoes XSRF-TOKEN like Angular.</summary>
    private sealed class CookieJar
    {
        private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

        public async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, object? body = null)
        {
            using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
            if (_cookies.Count > 0)
            {
                request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
            }

            if (method != HttpMethod.Get && _cookies.TryGetValue("XSRF-TOKEN", out var xsrf))
            {
                request.Headers.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(xsrf));
            }

            var response = await client.SendAsync(request, Ct);
            foreach (var cookie in response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [])
            {
                var pair = cookie.Split(';', 2)[0].Split('=', 2);
                _cookies[pair[0]] = pair[1];
            }

            return response;
        }
    }
}
