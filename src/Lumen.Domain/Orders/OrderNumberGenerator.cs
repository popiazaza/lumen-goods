using System.Security.Cryptography;

namespace Lumen.Domain.Orders;

/// <summary>
/// BR-ORD-01: order numbers look like <c>LG-7K3M9QX2</c>: a prefix plus 8 random Crockford base32 characters.
/// Random rather than sequential, so they reveal nothing about order volume and cannot be guessed in order.
/// The alphabet has no I, L, O or U, so a number read out over the phone cannot be confused (1/I/L, 0/O).
/// 32^8 ≈ 1.1 × 10^12 values; the database's unique index catches the rare collision and the caller draws again.
/// </summary>
/// <param name="nextIndex">Returns a random int in [0, maxExclusive). Defaults to a cryptographic source; tests inject their own.</param>
public sealed class OrderNumberGenerator(Func<int, int>? nextIndex = null)
{
    public const string Prefix = "LG-";
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int RandomLength = 8;

    private readonly Func<int, int> _nextIndex = nextIndex ?? RandomNumberGenerator.GetInt32;

    public string Next()
    {
        Span<char> chars = stackalloc char[RandomLength];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[_nextIndex(Alphabet.Length)];
        }

        return Prefix + new string(chars);
    }

    /// <summary>True for <c>LG-</c> plus exactly 8 alphabet characters (upper case), e.g. to reject a malformed route value early.</summary>
    public static bool IsWellFormed(string? value) =>
        value is { Length: 11 }
        && value.StartsWith(Prefix, StringComparison.Ordinal)
        && value.AsSpan(Prefix.Length).IndexOfAnyExcept(Alphabet) < 0;
}
