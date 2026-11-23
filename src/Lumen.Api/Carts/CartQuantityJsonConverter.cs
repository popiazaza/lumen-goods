using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Lumen.Api.Carts;

/// <summary>
/// Reads a cart quantity so that every value the cart refuses gets the same field error. A quantity that is not a
/// whole number in the int range (1.5, 2147483648, null, "x") would otherwise fail JSON binding and answer a bare
/// 400 "Bad Request"; it is read as 0 instead, which the [Range] check then refuses with the quantity message, as it
/// does 0, 11 or -1 (BR-CART-03).
/// </summary>
internal sealed class CartQuantityJsonConverter : JsonConverter<int>
{
    /// <summary>Outside 1 to <see cref="CartLine.MaxQuantity"/>, so validation names the field.</summary>
    private const int Unreadable = 0;

    public override bool HandleNull => true;

    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var number) ? number : Unreadable;
            case JsonTokenType.String:
                // Web defaults accept numbers in strings ("3"); anything else in a string is unreadable.
                return int.TryParse(reader.GetString(), NumberStyles.AllowLeadingSign, NumberFormatInfo.InvariantInfo, out var parsed)
                    ? parsed
                    : Unreadable;
            case JsonTokenType.Null or JsonTokenType.True or JsonTokenType.False:
                return Unreadable;
            default:
                // An object or array is not a value at all: left to JSON binding's plain 400.
                throw new JsonException("A quantity is a whole number.");
        }
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

/// <summary>
/// A custom converter hides the property's type from the OpenAPI generator, which would describe a quantity as any
/// value; this puts back what clients may send: a whole number from 1 to <see cref="CartLine.MaxQuantity"/>.
/// </summary>
internal sealed class CartQuantitySchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonPropertyInfo?.CustomConverter is CartQuantityJsonConverter)
        {
            schema.Type = JsonSchemaType.Integer;
            schema.Format = "int32";
            schema.Minimum = "1";
            schema.Maximum = CartLine.MaxQuantity.ToString(CultureInfo.InvariantCulture);
        }

        return Task.CompletedTask;
    }
}
