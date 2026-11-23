using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Infrastructure;

/// <summary>What one request may send to the API (section <c>RequestLimits</c>).</summary>
public sealed class RequestLimitsOptions
{
    public const string SectionName = "RequestLimits";

    /// <summary>
    /// The largest request body Kestrel reads, in bytes: 1 MB, as nginx's <c>client_max_body_size</c>. The biggest
    /// legitimate body (a Place order of 50 lines with an address) is a few kilobytes.
    /// </summary>
    [Range(1_024, 104_857_600)]
    public long MaxRequestBodySize { get; init; } = 1_048_576;
}

internal static class RequestLimits
{
    /// <summary>
    /// Kestrel's own body cap, so it holds without nginx: on Azure, Front Door routes /api straight to the API (DESIGN
    /// §8). A larger body is refused before it is read; the endpoint answers 413, which UseStatusCodePages turns into a
    /// problem. The in-process test server has no such limit, so the test runs the API on Kestrel.
    /// </summary>
    public static IServiceCollection AddRequestLimits(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RequestLimitsOptions>()
            .Bind(configuration.GetSection(RequestLimitsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<KestrelServerOptions>().Configure<IOptions<RequestLimitsOptions>>((kestrel, limits) =>
            kestrel.Limits.MaxRequestBodySize = limits.Value.MaxRequestBodySize);
        return services;
    }
}
