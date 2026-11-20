using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumen.Api.Infrastructure.Json;

/// <summary>
/// Writes money with exactly two decimals (49.90, never 49.9), as the storefront format requires. A decimal keeps its
/// scale through arithmetic, so the default serializer would print whatever scale the calculation happened to leave.
/// </summary>
public sealed class TwoDecimalJsonConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDecimal();

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteRawValue(value.ToString("0.00", CultureInfo.InvariantCulture), skipInputValidation: true);
}
