using Lumen.Api.Partners.Admin;
using Lumen.Api.Partners.Harbor;
using Lumen.Api.Partners.Kestrel;
using Lumen.Api.Partners.Mapping;
using Lumen.Api.Partners.Media;
using Lumen.Api.Partners.Ports;
using Lumen.Api.Partners.Sync;
using Lumen.Domain.Catalog;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Polly;

namespace Lumen.Api.Partners;

/// <summary>
/// Partner integration: anti-corruption adapters for each partner's published API, the catalogue sync jobs, the image
/// proxy and the admin partner status endpoints.
/// </summary>
internal static class PartnersFeature
{
    public static IServiceCollection AddPartners(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PartnersOptions>().Bind(configuration.GetSection(PartnersOptions.SectionName));
        services.AddOptions<FxOptions>().Bind(configuration.GetSection(FxOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<JobsOptions>().Bind(configuration.GetSection(JobsOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<MediaOptions>().Bind(configuration.GetSection(MediaOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<HarborOptions>().Bind(configuration.GetSection(HarborOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<KestrelOptions>().Bind(configuration.GetSection(KestrelOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<DescriptionSanitizer>();
        services.AddSingleton<CurrencyConverter>();
        services.AddSingleton<SnapshotMapper>();
        services.AddSingleton<PartnerRegistry>();
        services.AddScoped<PartnerSyncService>();

        // Harbor: JSON REST, X-Harbor-Key, 60 requests/minute per key shared by everything that calls it.
        services.AddSingleton<HarborRateGate>();
        services.AddTransient<HarborKeyHandler>();
        services.AddPartnerHttpClient<HarborOptions>(
                HarborCatalogSource.HttpClientName,
                (pipeline, sp) => pipeline.AddRateLimiter(sp.GetRequiredService<HarborRateGate>().Limiter))
            .AddHttpMessageHandler<HarborKeyHandler>();
        services.AddPartnerHttpClient<HarborOptions>(
                HarborCatalogSource.ImageHttpClientName,
                (pipeline, sp) => pipeline.AddRateLimiter(sp.GetRequiredService<HarborRateGate>().ImageLimiter))
            .AddHttpMessageHandler<HarborKeyHandler>();
        services.AddSingleton<HarborCatalogSource>();
        services.AddPartner(
            ProductSources.Harbor,
            HarborCatalogSource.HttpClientName,
            sp => sp.GetRequiredService<HarborCatalogSource>(),
            stock: null,
            sp => sp.GetRequiredService<IOptionsMonitor<HarborOptions>>().CurrentValue,
            imageHttpClientName: HarborCatalogSource.ImageHttpClientName);

        // Kestrel: XML feed (slow, flaky), CSV stock, Basic auth.
        services.AddTransient<KestrelBasicAuthHandler>();
        services.AddPartnerHttpClient<KestrelOptions>(KestrelCatalogSource.HttpClientName)
            .AddHttpMessageHandler<KestrelBasicAuthHandler>();
        services.AddSingleton<KestrelCatalogSource>();
        services.AddSingleton<KestrelStockSource>();
        services.AddPartner(
            ProductSources.Kestrel,
            KestrelCatalogSource.HttpClientName,
            sp => sp.GetRequiredService<KestrelCatalogSource>(),
            sp => sp.GetRequiredService<KestrelStockSource>(),
            sp => sp.GetRequiredService<IOptionsMonitor<KestrelOptions>>().CurrentValue,
            imageHttpClientName: KestrelCatalogSource.HttpClientName);

        services.AddSingleton<PartnerImageCache>();
        services.AddSingleton<PartnerImageProxy>();
        return services;
    }

    /// <summary>Admin partner status endpoints under /api/v1/admin.</summary>
    public static RouteGroupBuilder MapPartnerAdminEndpoints(this RouteGroupBuilder api) => api.MapPartnerAdmin();

    /// <summary>
    /// /media/{productId}/{index}: product images served by Lumen, so the browser never calls a partner (partner image
    /// URLs need the partner's credentials and are not reachable from outside the compose network).
    /// </summary>
    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app) => app.MapMedia();

    /// <summary>
    /// Registers one partner: its registration (adapters + settings) and its sync job, both as a keyed singleton (so
    /// tests can call RunOnceAsync) and as a hosted service.
    /// </summary>
    private static void AddPartner(
        this IServiceCollection services,
        string code,
        string httpClientName,
        Func<IServiceProvider, IPartnerCatalogSource> catalog,
        Func<IServiceProvider, IPartnerStockSource>? stock,
        Func<IServiceProvider, PartnerOptions> options,
        string imageHttpClientName)
    {
        services.AddSingleton(sp => new PartnerRegistration(code, httpClientName, catalog(sp), stock?.Invoke(sp), () => options(sp), imageHttpClientName));
        services.AddKeyedSingleton(code, (sp, _) => ActivatorUtilities.CreateInstance<PartnerSyncJob>(sp, sp.GetRequiredService<PartnerRegistry>().Get(code)));

        // Not AddHostedService: it de-duplicates by implementation type, which would keep only the first partner's job.
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<PartnerSyncJob>(code));
    }
}
