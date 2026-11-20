using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace KestrelOutfitters.StandIn;

/// <summary>
/// Contract: every request (images and unknown paths included) uses HTTP Basic authentication;
/// a failure is a 401 with an empty body.
/// </summary>
internal sealed class BasicAuthenticationMiddleware(RequestDelegate next, IOptions<KestrelOutfittersOptions> options)
{
    // The challenge is standard HTTP (RFC 9110 requires it on a 401) and keeps the body empty.
    private const string Challenge = "Basic realm=\"Kestrel Outfitters\", charset=\"UTF-8\"";

    private readonly byte[] _expected = Encoding.UTF8.GetBytes($"{options.Value.Username}:{options.Value.Password}");

    public Task InvokeAsync(HttpContext context)
    {
        if (HasValidCredentials(context.Request.Headers.Authorization))
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = Challenge;
        context.Response.ContentLength = 0;
        return Task.CompletedTask;
    }

    private bool HasValidCredentials(StringValues header)
    {
        if (header.Count != 1
            || !AuthenticationHeaderValue.TryParse(header[0], out var value)
            || !string.Equals(value.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            || value.Parameter is null)
        {
            return false;
        }

        var decoded = new byte[value.Parameter.Length];
        return Convert.TryFromBase64String(value.Parameter, decoded, out var length)
            && CryptographicOperations.FixedTimeEquals(decoded.AsSpan(0, length), _expected);
    }
}
