using System.Text;

namespace Lumen.Domain.Catalog;

/// <summary>
/// The storefront format wants attribute keys as lower camelCase English names. Partners send their own spellings
/// (Harbor "colour", Kestrel "Colour", "Fill Power"), so every adapter normalises keys here: synonyms first
/// (configurable), then lower camelCase.
/// </summary>
public static class AttributeKeys
{
    public static readonly IReadOnlyDictionary<string, string> DefaultSynonyms =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["colour"] = "color" };

    /// <returns>The normalised key, or null when nothing usable is left.</returns>
    public static string? Normalize(string? rawKey, IReadOnlyDictionary<string, string>? synonyms = null)
    {
        var words = SplitWords(rawKey);
        if (words.Count == 0)
        {
            return null;
        }

        var camel = ToLowerCamel(words);
        return (synonyms ?? DefaultSynonyms).TryGetValue(camel, out var synonym) ? synonym : camel;
    }

    /// <summary>Normalises every key; when two partner keys map to the same name, the first one wins.</summary>
    public static Dictionary<string, string> NormalizeAll(
        IEnumerable<KeyValuePair<string, string>> attributes, IReadOnlyDictionary<string, string>? synonyms = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
        {
            if (Normalize(key, synonyms) is { } normalized && !string.IsNullOrWhiteSpace(value))
            {
                result.TryAdd(normalized, value.Trim());
            }
        }

        return result;
    }

    private static List<string> SplitWords(string? raw)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return words;
        }

        var current = new StringBuilder();
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            var startsNewWord = char.IsUpper(c) && current.Length > 0 && i > 0 && char.IsLower(raw[i - 1]);
            if (!char.IsLetterOrDigit(c) || startsNewWord)
            {
                Flush();
                if (!char.IsLetterOrDigit(c))
                {
                    continue;
                }
            }

            current.Append(c);
        }

        Flush();
        return words;

        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
    }

    private static string ToLowerCamel(List<string> words)
    {
        var sb = new StringBuilder(words[0].ToLowerInvariant());
        foreach (var word in words.Skip(1))
        {
            sb.Append(char.ToUpperInvariant(word[0])).Append(word[1..].ToLowerInvariant());
        }

        return sb.ToString();
    }
}
