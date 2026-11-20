using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace HarborSupply.StandIn.Http;

/// <summary>Contract: every request, images included, needs X-Harbor-Key; a missing or wrong key is a 401.</summary>
internal sealed class ApiKeyMiddleware(RequestDelegate next, IOptions<HarborOptions> options)
{
    public const string HeaderName = "X-Harbor-Key";

    private readonly byte[] _expected = Encoding.UTF8.GetBytes(options.Value.ApiKey);

    public Task InvokeAsync(HttpContext context)
    {
        var supplied = Encoding.UTF8.GetBytes(context.Request.Headers[HeaderName].ToString());

        // Constant-time comparison, so response timing reveals nothing about how much of a guess was right.
        if (!CryptographicOperations.FixedTimeEquals(supplied, _expected))
        {
            return HarborErrors.WriteAsync(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Missing or invalid X-Harbor-Key header.");
        }

        return next(context);
    }
}
