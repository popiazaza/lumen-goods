namespace Lumen.Api.Catalog;

/// <summary>
/// Checks free text from clients before it reaches PostgreSQL. A text column cannot hold NUL (the query fails with
/// 22021, which would surface as a 500), and no catalogue field has a use for other control characters or for broken
/// UTF-16, so these are rejected as a 400 naming the field instead.
/// </summary>
public static class TextInput
{
    /// <summary>One-line text (names, search, attribute values): no control characters at all.</summary>
    public static bool IsValidSingleLine(string value) => IsValid(value, allowLineBreaksAndTabs: false);

    /// <summary>Multi-line text (descriptions): line breaks and tabs are fine, other control characters are not.</summary>
    public static bool IsValidMultiLine(string value) => IsValid(value, allowLineBreaksAndTabs: true);

    private static bool IsValid(string value, bool allowLineBreaksAndTabs)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsControl(c) && !(allowLineBreaksAndTabs && c is '\n' or '\r' or '\t'))
            {
                return false;
            }

            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }
}
