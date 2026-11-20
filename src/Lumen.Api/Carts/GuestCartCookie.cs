using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Carts;

/// <summary>
/// Reads and writes the guest cart cookie (BR-CART-01, A-48): the random token from <see cref="GuestCartToken"/>,
/// HttpOnly (scripts never see it), SameSite=Strict, Secure by the same <c>Auth:CookieSecurePolicy</c> as the session
/// cookie, and scoped to <c>/api</c> (only the API reads it). Its expiry is the cart's last change plus
/// <see cref="CartOptions.GuestCartLifetime"/>, the same moment the server stops honouring the cart, so the two never disagree.
/// </summary>
internal sealed class GuestCartCookie(IOptionsMonitor<CookieAuthenticationOptions> authCookies)
{
    public const string Path = "/api";

    /// <summary>The token from the request's cookie, or null when there is none or it is not one this API could have issued.</summary>
    public static string? Read(HttpContext http)
    {
        // A token is 43 base64url characters; anything else is not ours, and hashing it would only cost a lookup.
        return http.Request.Cookies.TryGetValue(GuestCartToken.CookieName, out var token)
            && token is { Length: GuestCartToken.TokenLength }
            && token.All(IsBase64UrlChar)
                ? token
                : null;
    }

    public void Write(HttpContext http, string token, DateTimeOffset expiresUtc) =>
        http.Response.Cookies.Append(GuestCartToken.CookieName, token, Options(http, expiresUtc));

    public void Expire(HttpContext http) =>
        http.Response.Cookies.Delete(GuestCartToken.CookieName, Options(http, expiresUtc: null));

    private CookieOptions Options(HttpContext http, DateTimeOffset? expiresUtc)
    {
        var securePolicy = authCookies.Get(IdentityConstants.ApplicationScheme).Cookie.SecurePolicy;
        return new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = securePolicy == CookieSecurePolicy.Always
                || (securePolicy == CookieSecurePolicy.SameAsRequest && http.Request.IsHttps),
            Path = Path,
            Expires = expiresUtc,

            // The cart is the shop working, not tracking, so a consent policy must not drop it.
            IsEssential = true,
        };
    }

    private static bool IsBase64UrlChar(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
}
