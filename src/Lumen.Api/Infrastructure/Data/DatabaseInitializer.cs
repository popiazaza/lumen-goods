using Lumen.Api.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Lumen.Api.Infrastructure.Data;

/// <summary>
/// Applies migrations and seeds the demo data on startup, so <c>docker compose up</c> needs no manual steps.
/// Seeding is idempotent: it only creates what is missing.
/// </summary>
internal static class DatabaseInitializer
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private const int MaxAttempts = 15;

    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseInitializer));
        var db = scope.ServiceProvider.GetRequiredService<LumenDbContext>();

        // The database container can accept connections a moment after it reports healthy; retry briefly.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync(cancellationToken);
                break;
            }
            catch (NpgsqlException ex) when (attempt < MaxAttempts)
            {
                logger.LogWarning(ex, "Database not reachable yet (attempt {Attempt}/{Max}); retrying", attempt, MaxAttempts);
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }

        await EnsureRolesAsync(scope.ServiceProvider);
        await SeedDemoAccountsAsync(scope.ServiceProvider, logger);
    }

    /// <summary>Roles are reference data that registration depends on, so they exist in every environment.</summary>
    private static async Task EnsureRolesAsync(IServiceProvider services)
    {
        var roles = services.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                EnsureSucceeded(await roles.CreateAsync(new IdentityRole<Guid>(role)), $"create role {role}");
            }
        }
    }

    private static async Task SeedDemoAccountsAsync(IServiceProvider services, ILogger logger)
    {
        var seed = services.GetRequiredService<IOptions<SeedOptions>>().Value;
        if (!seed.Enabled)
        {
            return;
        }

        var users = services.GetRequiredService<UserManager<AppUser>>();
        await EnsureUserAsync(users, seed.AdminEmail, seed.AdminPassword, Roles.Admin, logger);
        await EnsureUserAsync(users, seed.CustomerEmail, seed.CustomerPassword, Roles.Customer, logger);
    }

    private static async Task EnsureUserAsync(UserManager<AppUser> users, string email, string password, string role, ILogger logger)
    {
        if (await users.FindByEmailAsync(email) is not null)
        {
            return;
        }

        var user = new AppUser { UserName = email, Email = email, EmailConfirmed = true };
        EnsureSucceeded(await users.CreateAsync(user, password), $"create user {email}");
        EnsureSucceeded(await users.AddToRoleAsync(user, role), $"add {email} to {role}");
        logger.LogInformation("Seeded {Role} account {Email}", role, email);
    }

    private static void EnsureSucceeded(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Seeding failed to {action}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }
    }
}
