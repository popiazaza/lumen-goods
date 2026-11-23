using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Lumen.Api.Carts;
using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Infrastructure.RateLimiting;
using Lumen.Api.Infrastructure.Security;
using Lumen.Api.Wallets;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Validation;

namespace Lumen.Api.Identity;

public sealed record SessionResponse(bool IsAuthenticated, string? Email, IReadOnlyList<string> Roles)
{
    public static readonly SessionResponse Anonymous = new(false, null, []);
}

/// <summary>
/// <c>POST /api/v1/auth/register</c>. The password needs at least 8 characters with an upper-case letter, a lower-case
/// letter, a number and a symbol (<see cref="PasswordRules"/>); a refused one is a 400 validation problem under
/// <c>Password</c>, one plain sentence per rule it misses.
/// </summary>
public sealed record RegisterRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required,
     MinLength(PasswordRules.MinLength, ErrorMessage = "Use at least {1} characters."),
     MaxLength(PasswordRules.MaxLength, ErrorMessage = "Use at most {1} characters.")] string Password);

public sealed record SignInRequest([Required, EmailAddress] string Email, [Required] string Password);

/// <summary>
/// What sign-in and registration hand over to other features, bound with [AsParameters] so the handlers stay short:
/// the guest cart merge (BR-CART-01) and, at registration, the new customer's wallet (BR-PAY-06). Both are best effort:
/// the account and the session already exist, so a failure is logged and retried later (the guest cart merges at the
/// next sign-in, the wallet opens on first use) instead of failing a sign-in that has already happened. Services, not
/// input, so request validation skips it.
/// </summary>
[SkipValidation]
internal sealed record AfterSignIn(ICartMergeService CartMerge, WalletService Wallets, ILogger<AfterSignIn> Logger)
{
    public async Task MergeGuestCartAsync(HttpContext http, Guid customerId)
    {
        try
        {
            await CartMerge.MergeGuestCartAsync(http, customerId, http.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Could not merge the guest cart of customer {CustomerId}; it merges at the next sign-in", customerId);
        }
    }
}

internal static class AuthEndpoints
{
    private static readonly AppUser TimingPadUser = new();
    private static readonly Lazy<string> TimingPadHash =
        new(() => new PasswordHasher<AppUser>().HashPassword(TimingPadUser, Guid.NewGuid().ToString()));

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth");
        auth.MapGet("/session", GetSession);
        auth.MapPost("/register", Register).RequireRateLimiting(RateLimitPolicies.Register)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        auth.MapPost("/sign-in", SignIn).RequireRateLimiting(RateLimitPolicies.SignIn)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
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
        LumenDbContext db, [AsParameters] AfterSignIn afterSignIn)
    {
        var user = new AppUser { UserName = request.Email, Email = request.Email };

        // The account and its role are one change: a failure in between must not leave an account that is not a customer.
        await using (var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted))
        {
            var created = await users.CreateAsync(user, request.Password);
            if (!created.Succeeded)
            {
                // Keys match the request property names, as the built-in request validation reports them.
                return TypedResults.ValidationProblem(created.Errors
                    .GroupBy(e => e.Code.Contains("Password", StringComparison.Ordinal) ? nameof(RegisterRequest.Password) : nameof(RegisterRequest.Email))
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Description).Distinct(StringComparer.Ordinal).ToArray()));
            }

            var roleAdded = await users.AddToRoleAsync(user, Roles.Customer);
            if (!roleAdded.Succeeded)
            {
                throw new InvalidOperationException($"Could not assign the customer role: {string.Join("; ", roleAdded.Errors.Select(e => e.Description))}");
            }

            await transaction.CommitAsync(CancellationToken.None);
        }

        // BR-PAY-06: every new customer starts with a wallet. Idempotent and best effort: if it fails here, it opens on first use.
        await afterSignIn.Wallets.TryEnsureOpenAsync(user.Id);

        var session = await CompleteSignInAsync(http, user, signIn, users, antiforgery);
        await afterSignIn.MergeGuestCartAsync(http, user.Id);
        return TypedResults.Ok(session);
    }

    private static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> SignIn(
        SignInRequest request, HttpContext http, UserManager<AppUser> users, SignInManager<AppUser> signIn,
        IPasswordHasher<AppUser> hasher, IAntiforgery antiforgery, SignInGate gate, [AsParameters] AfterSignIn afterSignIn)
    {
        // One attempt per email at a time, from reading the account to saving its failed count, so the lockout below
        // counts every wrong password of a burst.
        using var turn = await gate.WaitTurnAsync(users.NormalizeEmail(request.Email) ?? "", http.RequestAborted);
        if (!turn.IsAcquired)
        {
            return InboundRateLimiting.TooManyRequests(http, retryAfter: null);
        }

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
        await afterSignIn.MergeGuestCartAsync(http, user.Id);
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
