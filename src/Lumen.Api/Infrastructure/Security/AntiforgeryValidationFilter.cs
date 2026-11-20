using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Lumen.Api.Infrastructure.Security;

/// <summary>
/// CSRF protection for the cookie-authenticated JSON API. Minimal APIs only validate antiforgery tokens for
/// form posts, so state-changing JSON calls are checked here: the request must echo the XSRF-TOKEN cookie in
/// the X-XSRF-TOKEN header (Angular's HttpClient does this automatically). SameSite=Strict cookies are
/// defence in depth, not a replacement.
/// </summary>
internal sealed class AntiforgeryValidationFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        if (HttpMethods.IsGet(httpContext.Request.Method) || HttpMethods.IsHead(httpContext.Request.Method)
            || HttpMethods.IsOptions(httpContext.Request.Method))
        {
            return await next(context);
        }

        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Missing or invalid XSRF token",
                detail: "Call GET /api/v1/auth/session first and send the XSRF-TOKEN cookie value in the X-XSRF-TOKEN header.");
        }

        return await next(context);
    }
}

internal static class XsrfToken
{
    public const string CookieName = "XSRF-TOKEN";

    /// <summary>
    /// Issues a request token for the current user in a cookie the SPA can read. Antiforgery tokens are bound
    /// to the signed-in user, so this runs again after sign-in and sign-out.
    /// </summary>
    public static void Issue(HttpContext httpContext, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(httpContext);
        var authOptions = httpContext.RequestServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme);

        httpContext.Response.Cookies.Append(CookieName, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Strict,
            Secure = authOptions.Cookie.SecurePolicy == CookieSecurePolicy.Always
                || (authOptions.Cookie.SecurePolicy == CookieSecurePolicy.SameAsRequest && httpContext.Request.IsHttps),
            Path = "/",
        });
    }
}
