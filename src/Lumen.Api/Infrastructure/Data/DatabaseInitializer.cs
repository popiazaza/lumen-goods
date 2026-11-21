using Lumen.Api.Catalog;
using Lumen.Api.Identity;
using Lumen.Api.Wallets;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
                // Npgsql's migrator reads the history table before EF creates it, and on an empty database EF logs that
                // failed SELECT as an Error, although the provider expects and handles it. Creating the table first (the
                // idempotent CREATE TABLE IF NOT EXISTS that MigrateAsync runs next anyway) keeps first starts free of
                // that false error, while real SQL errors are still logged. Skipped when the database itself does not
                // exist yet: MigrateAsync creates it.
                if (await db.Database.CanConnectAsync(cancellationToken))
                {
                    await db.GetService<IHistoryRepository>().CreateIfNotExistsAsync(cancellationToken);
                }

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
        await SeedDemoAccountsAsync(scope.ServiceProvider, logger, cancellationToken);
        await OwnProductSeed.SeedAsync(scope.ServiceProvider, logger, cancellationToken);
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

    private static async Task SeedDemoAccountsAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        var seed = services.GetRequiredService<IOptions<SeedOptions>>().Value;
        if (!seed.Enabled)
        {
            return;
        }

        var users = services.GetRequiredService<UserManager<AppUser>>();
        await EnsureUserAsync(users, seed.AdminEmail, seed.AdminPassword, Roles.Admin, logger);
        var customer = await EnsureUserAsync(users, seed.CustomerEmail, seed.CustomerPassword, Roles.Customer, logger);

        // BR-PAY-06 / A-38: the demo customer's wallet holds enough for the catalogue's most expensive items. Checked on
        // every start (not only when the account is created), so a database seeded before wallets existed gets one too.
        var walletOptions = services.GetRequiredService<IOptions<WalletOptions>>().Value;
        if (await services.GetRequiredService<WalletService>().OpenAsync(customer.Id, walletOptions.SeededCustomerBalance, cancellationToken))
        {
            logger.LogInformation("Seeded wallet of {Email} with {Balance:0.00} USD", seed.CustomerEmail, walletOptions.SeededCustomerBalance);
        }
    }

    private static async Task<AppUser> EnsureUserAsync(UserManager<AppUser> users, string email, string password, string role, ILogger logger)
    {
        if (await users.FindByEmailAsync(email) is { } existing)
        {
            return existing;
        }

        var user = new AppUser { UserName = email, Email = email, EmailConfirmed = true };
        EnsureSucceeded(await users.CreateAsync(user, password), $"create user {email}");
        EnsureSucceeded(await users.AddToRoleAsync(user, role), $"add {email} to {role}");
        logger.LogInformation("Seeded {Role} account {Email}", role, email);
        return user;
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
