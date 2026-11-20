namespace Lumen.Api.Partners.Mapping;

/// <summary>
/// A partner's category codes mapped to Lumen categories, from configuration (Harbor dept, Kestrel Group). Codes are
/// compared trimmed and case-insensitively. A code that is not mapped yields null: the product is not shown.
/// </summary>
public sealed class CategoryMap
{
    private readonly Dictionary<string, string> _map;

    public CategoryMap(IReadOnlyDictionary<string, string> map)
    {
        _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, category) in map)
        {
            // An empty value hides the code, so a mapping can be switched off with one environment variable.
            if (!string.IsNullOrWhiteSpace(category))
            {
                _map[code.Trim()] = category.Trim();
            }
        }
    }

    public string? Map(string? partnerCode) =>
        partnerCode is null ? null : _map.GetValueOrDefault(partnerCode.Trim());

    /// <summary>Stable text form, so a mapping change can be detected (and unchanged items re-mapped).</summary>
    public string Fingerprint() =>
        string.Join(";", _map.OrderBy(kv => kv.Key.ToUpperInvariant(), StringComparer.Ordinal)
            .Select(kv => $"{kv.Key.ToUpperInvariant()}={kv.Value}"));
}
