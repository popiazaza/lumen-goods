using System.Text.Json.Serialization;

namespace HarborSupply.StandIn.Orders;

/// <summary>
/// POST /v2/orders body. Every member is nullable so a missing field becomes a 400 with a message
/// from <see cref="Validate"/> rather than a serializer exception.
/// </summary>
internal sealed record OrderRequest(
    [property: JsonPropertyName("external_ref")] string? ExternalRef,
    [property: JsonPropertyName("lines")] IReadOnlyList<OrderLineRequest?>? Lines,
    [property: JsonPropertyName("ship_to")] ShipToRequest? ShipTo)
{
    /// <summary>Returns the first problem that makes this a 400, or null when the request is well formed.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(ExternalRef))
        {
            return "external_ref is required.";
        }

        if (Lines is not { Count: > 0 })
        {
            return "lines must contain at least one line.";
        }

        for (var i = 0; i < Lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(Lines[i]?.ItemCode))
            {
                return $"lines[{i}].item_code is required.";
            }

            if (Lines[i]!.Qty is not > 0)
            {
                return $"lines[{i}].qty must be a positive integer.";
            }
        }

        return ShipTo is null ? "ship_to is required." : ShipTo.Validate();
    }
}

internal sealed record OrderLineRequest(
    [property: JsonPropertyName("item_code")] string? ItemCode,
    [property: JsonPropertyName("qty")] int? Qty);

internal sealed record ShipToRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("line1")] string? Line1,
    [property: JsonPropertyName("line2")] string? Line2,
    [property: JsonPropertyName("city")] string? City,
    [property: JsonPropertyName("region")] string? Region,
    [property: JsonPropertyName("postal_code")] string? PostalCode,
    [property: JsonPropertyName("country_iso2")] string? CountryIso2)
{
    /// <summary>line2 is the only optional field: the spec's own sample sends it as null.</summary>
    public string? Validate()
    {
        (string Field, string? Value)[] required =
            [("name", Name), ("line1", Line1), ("city", City), ("region", Region), ("postal_code", PostalCode), ("country_iso2", CountryIso2)];
        foreach (var (field, value) in required)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return $"ship_to.{field} is required.";
            }
        }

        return CountryIso2 is { Length: 2 } && CountryIso2.All(char.IsAsciiLetter)
            ? null
            : "ship_to.country_iso2 must be a two-letter ISO 3166-1 code.";
    }
}

internal sealed record OrderResponse(
    [property: JsonPropertyName("order_id")] string OrderId,
    [property: JsonPropertyName("status")] string Status);
