using Lumen.Api.Partners.Harbor;
using Lumen.Api.Partners.Kestrel;
using Lumen.Api.Partners.Ports;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumen.Api.Orders;

/// <summary>
/// The order gateways of every partner, by partner code (BR-ORD-01). A third partner is one more adapter implementing
/// <see cref="IPartnerOrderGateway"/> and one registration in <see cref="PartnerOrderGatewayRegistration"/>.
/// </summary>
internal sealed class PartnerOrderGateways(IEnumerable<IPartnerOrderGateway> gateways)
{
    private readonly Dictionary<string, IPartnerOrderGateway> _byPartner =
        gateways.ToDictionary(g => g.Partner, StringComparer.OrdinalIgnoreCase);

    public IPartnerOrderGateway? Find(string partner) => _byPartner.GetValueOrDefault(partner);
}

internal static class PartnerOrderGatewayRegistration
{
    /// <summary>
    /// The Harbor and Kestrel order adapters. They use the partners' named HTTP clients (registered by AddPartners), so
    /// orders share Harbor's one 60-per-minute budget with the catalogue sync, and their credentials, circuit breaker
    /// and timeouts. Those clients never retry a POST themselves; the partner order job resends with the same reference.
    /// </summary>
    public static IServiceCollection AddPartnerOrderGateways(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IPartnerOrderGateway, HarborOrderGateway>();
        services.AddSingleton<IPartnerOrderGateway, KestrelOrderGateway>();
        services.AddSingleton<PartnerOrderGateways>();
        return services;
    }
}
