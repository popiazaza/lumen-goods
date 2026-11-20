namespace Lumen.Api.Partners.Mapping;

/// <summary>Everything an adapter's item mapping needs that comes from configuration.</summary>
internal sealed record PartnerMappingContext(
    Uri BaseUri,
    CategoryMap Categories,
    IReadOnlyDictionary<string, string> AttributeSynonyms,
    DescriptionSanitizer Sanitizer)
{
    /// <summary>The products table's SourceProductId length; longer partner ids are skipped as data errors.</summary>
    public const int MaxSourceProductIdLength = 64;
}
