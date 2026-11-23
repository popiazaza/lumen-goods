using System.Security.Claims;

namespace Lumen.Api.Identity;

/// <summary>The one place that reads the signed-in user's id from the session.</summary>
internal static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// The signed-in user's id, or null for a guest. ASP.NET Core Identity puts it in the NameIdentifier claim (its
    /// default <c>ClaimsIdentityOptions.UserIdClaimType</c>, which Lumen does not change).
    /// </summary>
    public static Guid? CustomerId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>
    /// Who the signed-in user is, for audit text an admin reads: their email (Identity's Email claim, or the user name,
    /// which is the email in Lumen), else their id.
    /// </summary>
    public static string SignedInAs(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.Email) is { Length: > 0 } email ? email
        : user.Identity?.Name is { Length: > 0 } name ? name
        : user.CustomerId()?.ToString() ?? "an unknown user";
}
