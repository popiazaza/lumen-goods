using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarborSupply.StandIn.Catalogue;

/// <summary>
/// The opaque next_cursor: base64url JSON of the last item's (updated_at, item_code). Paging is a keyset over that
/// pair, so items changing between pages cannot shift a page boundary the way an offset cursor would.
/// </summary>
internal readonly record struct PageCursor(DateTime UpdatedAt, string ItemCode)
{
    public string Encode() =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new CursorJson(UpdatedAt.ToString("O", CultureInfo.InvariantCulture), ItemCode)));

    public static bool TryDecode(string value, [NotNullWhen(true)] out PageCursor? cursor)
    {
        cursor = null;
        try
        {
            var json = JsonSerializer.Deserialize<CursorJson>(Base64Url.DecodeFromChars(value));
            if (json is { U: { } u, C: { Length: > 0 } c }
                && DateTime.TryParseExact(u, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedAt)
                && updatedAt.Kind == DateTimeKind.Utc)
            {
                cursor = new PageCursor(updatedAt, c);
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            // Not base64url or not our JSON: the caller answers 400.
        }

        return cursor is not null;
    }

    private sealed record CursorJson([property: JsonPropertyName("u")] string? U, [property: JsonPropertyName("c")] string? C);
}
