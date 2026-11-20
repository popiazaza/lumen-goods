using Microsoft.AspNetCore.Identity;

namespace Lumen.Api.Identity;

public sealed class AppUser : IdentityUser<Guid>;

public static class Roles
{
    public const string Admin = "admin";
    public const string Customer = "customer";

    public static readonly IReadOnlyList<string> All = [Admin, Customer];
}

public static class Policies
{
    public const string Admin = "admin";
}
