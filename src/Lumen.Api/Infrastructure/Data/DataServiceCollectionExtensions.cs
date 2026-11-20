using Microsoft.EntityFrameworkCore;

namespace Lumen.Api.Infrastructure.Data;

internal static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddLumenData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Lumen")
            ?? throw new InvalidOperationException("Connection string 'Lumen' is not configured.");

        services.AddDbContext<LumenDbContext>(options => options.UseNpgsql(connectionString));
        services.AddOptions<SeedOptions>()
            .Bind(configuration.GetSection(SeedOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }
}
