using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Lumen.Api.Carts;
using Lumen.Api.Infrastructure.Security;
using Lumen.Api.Wallets;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Identity;

public sealed record SessionResponse(bool IsAuthenticated, string? Email, IReadOnlyList<string> Roles)
{
    public static readonly SessionResponse Anonymous = new(false, null, []);
}

public sealed record RegisterRequest([Required, EmailAddress, MaxLength(256)] string Email, [Required, MinLength(8), MaxLength(128)] string Password);

public sealed record SignInRequest([Required, EmailAddress] string Email, [Required] string Password);

/// <summary>
/// What sign-in and registration hand over to other features, bound with [AsParameters] so the handlers stay short:
/// the guest cart merge (BR-CART-01) and, at registration, the new customer's wallet (BR-PAY-06).
/// </summary>
internal sealed record AfterSignIn(ICartMergeService CartMerge, WalletAccounts Wallets, IOptions<WalletOptions> WalletOptions);

internal static class AuthEndpoints
{
    private static readonly AppUser TimingPadUser = new();
    private static readonly Lazy<string> TimingPadHash =
        new(() => new PasswordHasher<AppUser>().HashPassword(TimingPadUser, Guid.NewGuid().ToString()));

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth");
        auth.MapGet("/session", GetSession);
        auth.MapPost("/register", Register);
        auth.MapPost("/sign-in", SignIn);
        auth.MapPost("/sign-out", SignOut);
        return api;
    }

    /// <summary>Current user (or anonymous) plus a fresh XSRF token. The SPA calls this on start-up.</summary>
    private static async Task<Ok<SessionResponse>> GetSession(HttpContext http, UserManager<AppUser> users, IAntiforgery antiforgery)
    {
        XsrfToken.Issue(http, antiforgery);
        return TypedResults.Ok(await DescribeAsync(http.User, users));
    }

    private static async Task<Results<Ok<SessionResponse>, ValidationProblem>> Register(
        RegisterRequest request, HttpContext http, UserManager<AppUser> users, SignInManager<AppUser> signIn, IAntiforgery antiforgery,
        [AsParameters] AfterSignIn afterSignIn)
    {
        var user = new AppUser { UserName = request.Email, Email = request.Email };
        var created = await users.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            // Keys match the request property names, as the built-in request validation reports them.
            return TypedResults.ValidationProblem(created.Errors
                .GroupBy(e => e.Code.Contains("Password", StringComparison.Ordinal) ? nameof(RegisterRequest.Password) : nameof(RegisterRequest.Email))
                .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
        }

        var roleAdded = await users.AddToRoleAsync(user, Roles.Customer);
        if (!roleAdded.Succeeded)
        {
            throw new InvalidOperationException($"Could not assign the customer role: {string.Join("; ", roleAdded.Errors.Select(e => e.Description))}");
        }

        // BR-PAY-06: every new customer starts with a wallet (idempotent, so a retried registration cannot open two).
        await afterSignIn.Wallets.OpenAsync(user.Id, afterSignIn.WalletOptions.Value.NewCustomerBalance, http.RequestAborted);

        var session = await CompleteSignInAsync(http, user, signIn, users, antiforgery);
        await afterSignIn.CartMerge.MergeGuestCartAsync(http, user.Id, http.RequestAborted);
        return TypedResults.Ok(session);
    }

    private static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> SignIn(
        SignInRequest request, HttpContext http, UserManager<AppUser> users, SignInManager<AppUser> signIn,
        IPasswordHasher<AppUser> hasher, IAntiforgery antiforgery, [AsParameters] AfterSignIn afterSignIn)
    {
        var user = await users.FindByEmailAsync(request.Email);
        if (user is null)
        {
            // Hash anyway so an unknown email takes as long as a wrong password and timing doesn't reveal accounts.
            hasher.VerifyHashedPassword(TimingPadUser, TimingPadHash.Value, request.Password);
        }

        var result = user is null
            ? Microsoft.AspNetCore.Identity.SignInResult.Failed
            : await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many failed attempts", detail: "The account is locked for a few minutes.");
        }

        if (!result.Succeeded || user is null)
        {
            // Same answer (and similar timing) for an unknown email and a wrong password. Registration ("email
            // already taken") and the lockout message still reveal that an account exists; that trade-off is
            // normal for a shop, where telling a customer they already have an account is the expected UX.
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Email or password is incorrect");
        }

        var session = await CompleteSignInAsync(http, user, signIn, users, antiforgery);
        await afterSignIn.CartMerge.MergeGuestCartAsync(http, user.Id, http.RequestAborted);
        return TypedResults.Ok(session);
    }

    private static async Task<Ok<SessionResponse>> SignOut(HttpContext http, SignInManager<AppUser> signIn, IAntiforgery antiforgery)
    {
        await signIn.SignOutAsync();
        http.User = new ClaimsPrincipal(new ClaimsIdentity());
        XsrfToken.Issue(http, antiforgery);
        return TypedResults.Ok(SessionResponse.Anonymous);
    }

    private static async Task<SessionResponse> CompleteSignInAsync(
        HttpContext http, AppUser user, SignInManager<AppUser> signIn, UserManager<AppUser> users, IAntiforgery antiforgery)
    {
        await signIn.SignInAsync(user, isPersistent: true);

        // The XSRF token must match the new identity, so issue it for the signed-in principal.
        http.User = await signIn.CreateUserPrincipalAsync(user);
        XsrfToken.Issue(http, antiforgery);
        return await DescribeAsync(http.User, users);
    }

    private static async Task<SessionResponse> DescribeAsync(ClaimsPrincipal principal, UserManager<AppUser> users)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return SessionResponse.Anonymous;
        }

        var user = await users.GetUserAsync(principal);
        if (user is null)
        {
            return SessionResponse.Anonymous;
        }

        var roles = await users.GetRolesAsync(user);
        return new SessionResponse(true, user.Email, [.. roles.Order(StringComparer.Ordinal)]);
    }
}
