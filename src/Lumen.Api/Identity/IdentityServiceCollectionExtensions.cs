using Lumen.Api.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace Lumen.Api.Identity;

internal static class IdentityServiceCollectionExtensions
{
    public const string AntiforgeryCookieName = "lumen_af";
    public const string SessionCookieName = "lumen_session";

    /// <summary>
    /// Cookie authentication with ASP.NET Core Identity. The SPA and the API share one origin (nginx proxies
    /// /api), so an HttpOnly cookie keeps the session out of JavaScript's reach; no tokens live in the browser.
    /// </summary>
    public static IServiceCollection AddLumenIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        // Always in production (behind TLS); SameAsRequest only for plain-HTTP localhost runs (compose, dev).
        var securePolicy = configuration.GetValue("Auth:CookieSecurePolicy", CookieSecurePolicy.Always);

        services
            .AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 8;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<LumenDbContext>()
            .AddSignInManager();

        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = SessionCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = securePolicy;
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;

            // An API answers with status codes instead of redirecting to a login page.
            options.Events.OnRedirectToLogin = context => SetStatus(context, StatusCodes.Status401Unauthorized);
            options.Events.OnRedirectToAccessDenied = context => SetStatus(context, StatusCodes.Status403Forbidden);
        });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Admin, policy => policy.RequireRole(Roles.Admin));

        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-XSRF-TOKEN";
            options.Cookie.Name = AntiforgeryCookieName;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = securePolicy;
        });

        AddDataProtectionKeys(services, configuration);
        return services;
    }

    /// <summary>
    /// The session cookie and the XSRF tokens are encrypted with Data Protection keys. In a container the default key
    /// store is lost with the container, signing everyone out and failing every open page's next POST, so compose keeps
    /// the keys on a volume (<c>DataProtection:KeysPath</c>). Unset (tests, <c>dotnet run</c>), the framework default
    /// applies: the user profile when there is one, otherwise keys in memory.
    /// </summary>
    private static void AddDataProtectionKeys(IServiceCollection services, IConfiguration configuration)
    {
        var keysPath = configuration["DataProtection:KeysPath"];
        if (string.IsNullOrWhiteSpace(keysPath))
        {
            return;
        }

        // A fixed application name, so the keys still apply if the app moves to another directory or image layout.
        services.AddDataProtection()
            .SetApplicationName("Lumen.Api")
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
    }

    private static Task SetStatus(Microsoft.AspNetCore.Authentication.RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }
}
