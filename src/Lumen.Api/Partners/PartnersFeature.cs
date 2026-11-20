namespace Lumen.Api.Partners;

/// <summary>
/// Partner integration: anti-corruption adapters for each partner's published API, the catalogue sync jobs, the image
/// proxy and the admin partner status endpoints.
/// </summary>
internal static class PartnersFeature
{
    public static IServiceCollection AddPartners(this IServiceCollection services, IConfiguration configuration) => services;

    /// <summary>Admin partner status endpoints under /api/v1/admin.</summary>
    public static RouteGroupBuilder MapPartnerAdminEndpoints(this RouteGroupBuilder api) => api;

    /// <summary>
    /// /media/{productId}/{index}: product images served by Lumen, so the browser never calls a partner (partner image
    /// URLs need the partner's credentials and are not reachable from outside the compose network).
    /// </summary>
    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app) => app;
}
