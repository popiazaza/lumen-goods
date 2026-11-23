using Microsoft.AspNetCore.Identity;

namespace Lumen.Api.Identity;

/// <summary>
/// The password rules for new accounts (A-50), set here and nowhere else in the API: Identity's options come from
/// <see cref="Apply"/>, and <see cref="PlainIdentityErrorDescriber"/> words what it refuses. The register page's hint
/// and client-side check (src/web/src/app/features/account/password-rules.ts) state the same rules for UX; the API
/// checks again and its answer wins.
/// </summary>
internal static class PasswordRules
{
    public const int MinLength = 8;

    public const int MaxLength = 128;

    public static void Apply(PasswordOptions options)
    {
        options.RequiredLength = MinLength;
        options.RequireUppercase = true;
        options.RequireLowercase = true;
        options.RequireDigit = true;
        options.RequireNonAlphanumeric = true;
        options.RequiredUniqueChars = 1;
    }
}

/// <summary>
/// Plain, customer-facing wording for the Identity errors registration can show, instead of the framework's
/// ("Passwords must have at least one non alphanumeric character."). Each one says what to do.
/// </summary>
internal sealed class PlainIdentityErrorDescriber : IdentityErrorDescriber
{
    public const string EmailTaken = "There is already an account with this email address. Sign in instead.";

    public override IdentityError PasswordTooShort(int length) =>
        new() { Code = nameof(PasswordTooShort), Description = $"Use at least {length} characters." };

    public override IdentityError PasswordRequiresUpper() =>
        new() { Code = nameof(PasswordRequiresUpper), Description = "Add an upper-case letter (A-Z)." };

    public override IdentityError PasswordRequiresLower() =>
        new() { Code = nameof(PasswordRequiresLower), Description = "Add a lower-case letter (a-z)." };

    public override IdentityError PasswordRequiresDigit() =>
        new() { Code = nameof(PasswordRequiresDigit), Description = "Add a number (0-9)." };

    public override IdentityError PasswordRequiresNonAlphanumeric() =>
        new() { Code = nameof(PasswordRequiresNonAlphanumeric), Description = "Add a symbol, such as ! or #." };

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) =>
        new() { Code = nameof(PasswordRequiresUniqueChars), Description = $"Use at least {uniqueChars} different characters." };

    // The user name is the email, so a taken email fails both checks; both say the same thing (the endpoint drops the repeat).
    public override IdentityError DuplicateEmail(string email) =>
        new() { Code = nameof(DuplicateEmail), Description = EmailTaken };

    public override IdentityError DuplicateUserName(string userName) =>
        new() { Code = nameof(DuplicateUserName), Description = EmailTaken };

    public override IdentityError InvalidEmail(string? email) =>
        new() { Code = nameof(InvalidEmail), Description = "Enter a valid email address, like name@example.com." };

    public override IdentityError InvalidUserName(string? userName) =>
        new() { Code = nameof(InvalidUserName), Description = "Enter a valid email address, like name@example.com." };
}
