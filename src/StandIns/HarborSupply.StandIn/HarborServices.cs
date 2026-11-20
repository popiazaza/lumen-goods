using HarborSupply.StandIn.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HarborSupply.StandIn;

internal static class HarborServices
{
    /// <summary>Options, clock and database: what both the HTTP service and the CLI need.</summary>
    public static IServiceCollection AddHarborCore(this IServiceCollection services)
    {
        services.AddOptions<HarborOptions>()
            .BindConfiguration(HarborOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        // Resolved when the first context is created, so settings supplied by tests or the command line apply.
        services.AddDbContextFactory<HarborDbContext>((provider, db) =>
        {
            var path = provider.GetRequiredService<IOptions<HarborOptions>>().Value.DatabasePath;
            var contentRoot = provider.GetRequiredService<IHostEnvironment>().ContentRootPath;
            db.UseSqlite(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(path, contentRoot) }.ToString());
        });

        return services;
    }
}
