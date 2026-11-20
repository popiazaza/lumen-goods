using System.Text.RegularExpressions;
using Lumen.Api.Catalog;
using Lumen.Domain.Catalog;
using Lumen.Domain.Money;

namespace Lumen.Api.Admin;

/// <summary>The checked, normalised editable fields of an own product.</summary>
internal sealed record OwnProductFields(
    string Name,
    string Description,
    string Category,
    decimal Price,
    Dictionary<string, string> Attributes,
    List<ProductImage> Images,
    int OnHand);

/// <summary>
/// Input rules for the admin own-products form. Errors are keyed by the JSON field name so the form can show each
/// message next to its field.
/// </summary>
internal static partial class OwnProductValidation
{
    public const int MaxNameLength = 300;
    public const int MaxDescriptionLength = 5000;
    public const int MaxAttributes = 20;
    public const int MaxAttributeKeyLength = 40;
    public const int MaxAttributeValueLength = 200;
    public const int MaxImages = 8;
    public const int MaxOnHand = 1_000_000;

    /// <summary>The products.PriceUsd column is numeric(12,2).</summary>
    public const decimal MaxPrice = 9_999_999_999.99m;

    public static Dictionary<string, string[]> Validate(IOwnProductInput input, OwnMediaLibrary media, out OwnProductFields? fields)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Fail(string field, string message) => errors[field] = [message];

        var name = input.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            Fail("name", "Name is required.");
        }
        else if (name.Length > MaxNameLength)
        {
            Fail("name", $"Name can be at most {MaxNameLength} characters.");
        }
        else if (!TextInput.IsValidSingleLine(name))
        {
            Fail("name", "Name cannot contain control characters such as tabs or line breaks.");
        }

        var description = input.Description?.Trim() ?? "";
        if (description.Length > MaxDescriptionLength)
        {
            Fail("description", $"Description can be at most {MaxDescriptionLength} characters.");
        }
        else if (!TextInput.IsValidMultiLine(description))
        {
            Fail("description", "Description cannot contain control characters other than line breaks and tabs.");
        }

        var category = input.Category?.Trim().ToLowerInvariant();
        if (!ProductCategories.IsValid(category))
        {
            Fail("category", $"Category must be one of: {string.Join(", ", ProductCategories.All)}.");
        }

        if (input.Price is not { } price)
        {
            Fail("price", "Price is required.");
        }
        else if (price <= 0)
        {
            Fail("price", "Price must be greater than 0.");
        }
        else if (MoneyMath.RoundToCents(price) != price)
        {
            Fail("price", "Price can have at most 2 decimal places.");
        }
        else if (price > MaxPrice)
        {
            Fail("price", $"Price can be at most {MaxPrice:0.00}.");
        }

        if (input.OnHand is not { } onHand)
        {
            Fail("onHand", "On hand is required.");
        }
        else if (onHand is < 0 or > MaxOnHand)
        {
            Fail("onHand", $"On hand must be from 0 to {MaxOnHand}.");
        }

        var attributes = NormaliseAttributes(input.Attributes, Fail);
        var images = CheckImages(input.Images, media, Fail);

        fields = errors.Count == 0
            ? new OwnProductFields(name, description, category!, input.Price!.Value, attributes, images, input.OnHand!.Value)
            : null;
        return errors;
    }

    /// <summary>Upper-cases a SKU the admin typed, or returns null when it is not usable.</summary>
    public static string? NormaliseSku(string? sku)
    {
        var normalised = sku?.Trim().ToUpperInvariant();
        return normalised is not null && SkuPattern().IsMatch(normalised) ? normalised : null;
    }

    public static string NewSku() => $"LG-{Guid.NewGuid():N}"[..11].ToUpperInvariant();

    /// <summary>
    /// The storefront format wants lower camelCase English keys, so admin keys go through the same normaliser as partner
    /// keys ("Fill Power" → "fillPower", "Colour" → "color"). Two keys that end up the same are a form error rather than
    /// one silently winning.
    /// </summary>
    private static Dictionary<string, string> NormaliseAttributes(Dictionary<string, string>? raw, Action<string, string> fail)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (raw is null)
        {
            return result;
        }

        if (raw.Count > MaxAttributes)
        {
            fail("attributes", $"A product can have at most {MaxAttributes} attributes.");
            return result;
        }

        foreach (var (rawKey, rawValue) in raw)
        {
            var key = TextInput.IsValidSingleLine(rawKey) ? AttributeKeys.Normalize(rawKey) : null;
            var value = rawValue?.Trim() ?? "";
            if (key is null || key.Length > MaxAttributeKeyLength)
            {
                fail("attributes", $"Attribute name '{rawKey}' needs letters or digits, at most {MaxAttributeKeyLength} characters and no control characters.");
            }
            else if (value.Length is 0 or > MaxAttributeValueLength)
            {
                fail("attributes", $"Attribute '{key}' needs a value of 1 to {MaxAttributeValueLength} characters.");
            }
            else if (!TextInput.IsValidSingleLine(value))
            {
                fail("attributes", $"Attribute '{key}' cannot contain control characters.");
            }
            else if (!result.TryAdd(key, value))
            {
                fail("attributes", $"Two attributes are both named '{key}'.");
            }
        }

        return result;
    }

    private static List<ProductImage> CheckImages(List<string>? files, OwnMediaLibrary media, Action<string, string> fail)
    {
        if (files is null || files.Count == 0)
        {
            return [];
        }

        if (files.Count > MaxImages)
        {
            fail("images", $"A product can have at most {MaxImages} images.");
            return [];
        }

        var unknown = files.Where(f => !media.Exists(f)).ToList();
        if (unknown.Count > 0)
        {
            fail("images", $"Not in the image library: {string.Join(", ", unknown)}.");
            return [];
        }

        if (files.Distinct(StringComparer.Ordinal).Count() != files.Count)
        {
            fail("images", "The same image is listed twice.");
            return [];
        }

        return [.. files.Select((file, index) => new ProductImage(OwnMediaLibrary.ToSourceUrl(file), IsPrimary: index == 0))];
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{1,31}$")]
    private static partial Regex SkuPattern();
}
