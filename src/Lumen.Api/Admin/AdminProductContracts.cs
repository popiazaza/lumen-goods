using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using Lumen.Api.Catalog;
using Lumen.Api.Infrastructure.Json;
using Lumen.Domain.Catalog;
using Microsoft.AspNetCore.Mvc;

namespace Lumen.Api.Admin;

/// <summary>
/// One of Lumen's own products as the admin pages see it: the storefront fields plus stock and the concurrency
/// <see cref="Version"/>. Admin-only, so it may show what the storefront format leaves out.
/// </summary>
/// <param name="Sku">The product's own id (<see cref="Product.SourceProductId"/>); fixed once created.</param>
/// <param name="Reserved">Units held by orders awaiting payment; on hand can never go below this.</param>
/// <param name="Availability">What the storefront shows: on hand minus reserved, as status and quantity.</param>
/// <param name="CreatedUtc">When the product was created; the storefront's "newest" sort uses it.</param>
/// <param name="Version">Opaque token: send it back unchanged with an update. A stale one gets 409.</param>
public sealed record AdminProductDto(
    string Id,
    string Sku,
    string Name,
    string Description,
    string Category,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal? Price,
    IReadOnlyList<AdminImageDto> Images,
    IReadOnlyDictionary<string, string> Attributes,
    int OnHand,
    int Reserved,
    AvailabilityDto Availability,
    bool IsActive,
    bool Sellable,
    DateTime CreatedUtc,
    DateTime LastUpdatedUtc,
    string Version);

/// <param name="File">File name in the own-media library (what create and update requests send).</param>
/// <param name="Url">Absolute URL the browser loads the image from (the same URL the storefront uses).</param>
public sealed record AdminImageDto(string File, string Url, bool IsPrimary);

/// <summary>An image in the own-media library that products can use.</summary>
public sealed record OwnMediaFileDto(string File, string Url);

/// <summary>The editable fields shared by create and update.</summary>
public interface IOwnProductInput
{
    string? Name { get; }

    string? Description { get; }

    string? Category { get; }

    decimal? Price { get; }

    Dictionary<string, string>? Attributes { get; }

    /// <summary>Own-media file names, in display order; the first is the primary image.</summary>
    List<string>? Images { get; }

    int? OnHand { get; }
}

/// <param name="Sku">Optional; upper-case letters, digits and dashes. Generated (LG-XXXXXXXX) when omitted.</param>
public sealed record CreateOwnProductRequest(
    string? Sku,
    string? Name,
    string? Description,
    string? Category,
    decimal? Price,
    Dictionary<string, string>? Attributes,
    List<string>? Images,
    int? OnHand) : IOwnProductInput;

/// <param name="Version">The <see cref="AdminProductDto.Version"/> the edit is based on.</param>
public sealed record UpdateOwnProductRequest(
    string? Version,
    string? Name,
    string? Description,
    string? Category,
    decimal? Price,
    Dictionary<string, string>? Attributes,
    List<string>? Images,
    int? OnHand) : IOwnProductInput;

public sealed class AdminProductListParameters
{
    [FromQuery(Name = "q"), Description("Case-insensitive text matched literally against name and SKU.")]
    public string? Q { get; set; }

    [FromQuery(Name = "status"), Description("active, inactive or all (default).")]
    public string? Status { get; set; }

    [FromQuery(Name = "category"), Description("Optional category.")]
    public string? Category { get; set; }

    [FromQuery(Name = "page"), Description("1-based page number. Default 1.")]
    public string? Page { get; set; }

    [FromQuery(Name = "pageSize"), Description("Items per page, 1 to 96. Default 24.")]
    public string? PageSize { get; set; }
}

internal static class AdminProductMapping
{
    public static AdminProductDto ToAdminDto(this Product product, AvailabilityPolicy policy, string publicBaseUrl)
    {
        // Built from the storefront format so image URLs and availability can never differ between the two.
        var shown = product.ToDto(policy, publicBaseUrl);
        return new AdminProductDto(
            Id: shown.Id,
            Sku: product.SourceProductId,
            Name: product.Name,
            Description: product.Description,
            Category: product.Category,
            Price: shown.Price?.Amount,
            Images: [.. shown.Images.Zip(product.Images, (image, stored) =>
                new AdminImageDto(OwnMediaLibrary.FileOf(stored.SourceUrl) ?? "", image.Url, image.IsPrimary))],
            Attributes: product.Attributes,
            OnHand: product.OnHand ?? 0,
            Reserved: product.Reserved,
            Availability: shown.Availability,
            IsActive: product.IsActive,
            Sellable: shown.Sellable,
            CreatedUtc: product.FirstSeenUtc.UtcDateTime,
            LastUpdatedUtc: shown.LastUpdatedUtc,
            Version: FormatVersion(product.Version));
    }

    public static string FormatVersion(uint version) => version.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseVersion(string? value, out uint version) =>
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out version);
}
