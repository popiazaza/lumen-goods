using System.ComponentModel.DataAnnotations;

namespace Lumen.Api.Infrastructure.Data;

/// <summary>
/// Demo accounts created on first run (listed in the README). Off by default so a production deployment never
/// gets an admin with a published password; Development, docker compose and the tests switch it on.
/// </summary>
public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public bool Enabled { get; init; }

    [Required, EmailAddress] public string AdminEmail { get; init; } = "";
    [Required, MinLength(8)] public string AdminPassword { get; init; } = "";
    [Required, EmailAddress] public string CustomerEmail { get; init; } = "";
    [Required, MinLength(8)] public string CustomerPassword { get; init; } = "";
}
