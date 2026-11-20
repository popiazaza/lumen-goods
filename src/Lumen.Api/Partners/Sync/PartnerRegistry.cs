using Lumen.Api.Partners.Ports;

namespace Lumen.Api.Partners.Sync;

/// <summary>
/// One partner as the rest of Lumen sees it: its adapters, its HTTP client and its settings. A third partner is a new
/// adapter, a configuration section and one registration in <see cref="PartnersFeature"/>.
/// </summary>
/// <param name="Stock">Null when the catalogue carries stock (Harbor).</param>
internal sealed record PartnerRegistration(
    string Code,
    string HttpClientName,
    IPartnerCatalogSource Catalog,
    IPartnerStockSource? Stock,
    Func<PartnerOptions> Options);

internal sealed class PartnerRegistry(IEnumerable<PartnerRegistration> registrations)
{
    public IReadOnlyList<PartnerRegistration> All { get; } = [.. registrations];

    public PartnerRegistration? Find(string? code) =>
        All.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));

    public PartnerRegistration Get(string code) =>
        Find(code) ?? throw new InvalidOperationException($"Partner '{code}' is not registered.");
}
