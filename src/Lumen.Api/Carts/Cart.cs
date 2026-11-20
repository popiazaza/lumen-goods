using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Lumen.Api.Catalog;
using Lumen.Domain.Catalog;

namespace Lumen.Api.Carts;

/// <summary>
/// A shopping cart (BR-CART-01): either a guest's, found by the random token in the guest cart cookie, or a
/// customer's saved cart. Never both (a check constraint): signing in merges the guest cart into the customer's and
/// deletes it. Server-side, so prices and notices are always worked out by the API, never by the browser.
/// </summary>
public sealed class Cart
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The owner of a saved cart; null for a guest cart. Unique: one saved cart per customer.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>
    /// SHA-256 of the guest cookie's random token (<see cref="GuestCartToken"/>); null for a customer's cart. Unique.
    /// The database holds only the hash, so a leaked backup cannot be replayed as someone's cart cookie.
    /// </summary>
    public byte[]? AnonymousTokenHash { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>Last change to the cart or its lines; a guest cart untouched for <see cref="CartOptions.GuestCartLifetime"/> may be deleted.</summary>
    public DateTimeOffset UpdatedUtc { get; set; }

    /// <summary>PostgreSQL xmin: two tabs changing the same cart cannot silently overwrite each other.</summary>
    public uint Version { get; set; }

    public List<CartLine> Lines { get; set; } = [];
}

/// <summary>
/// One product in a cart. BR-CART-02: the line keeps what the customer saw when adding it (price, availability,
/// sellable), so the cart can show a notice when the product changed since; totals always use the current price.
/// BR-CART-03: 1 to <see cref="MaxQuantity"/> units (a check constraint), one line per product (unique index).
/// </summary>
public sealed class CartLine
{
    /// <summary>BR-CART-03: most units of one product per line. The database check constraint uses the same value.</summary>
    public const int MaxQuantity = 10;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid CartId { get; set; }

    public Guid ProductId { get; set; }

    public Product? Product { get; set; }

    public int Quantity { get; set; }

    /// <summary>
    /// The USD unit price the customer last saw for this line: when it was added, or when they acknowledged a notice.
    /// A different current price (up or down) shows "Price changed from X to Y" (BR-CART-02).
    /// </summary>
    public decimal UnitPriceSeenUsd { get; set; }

    /// <summary>The availability the customer last saw; a worse one now shows a notice (BR-CART-02).</summary>
    public AvailabilityStatus AvailabilitySeen { get; set; }

    /// <summary>Whether the product was sellable when last seen; a product no longer sellable shows "No longer available".</summary>
    public bool SellableSeen { get; set; }

    public DateTimeOffset AddedUtc { get; set; }

    /// <summary>
    /// When the customer last acknowledged this line's notices. Acknowledging also copies the current price and
    /// availability into the *Seen columns, so the notice goes away until the next change.
    /// </summary>
    public DateTimeOffset? NoticeAcknowledgedUtc { get; set; }

    /// <summary>
    /// BR-CART-01: the quantity the customer asked for before it was capped (merge of a guest cart: summed quantities
    /// above the known stock or above <see cref="MaxQuantity"/>). Shown as "quantity limited" until acknowledged;
    /// always greater than <see cref="Quantity"/> when set.
    /// </summary>
    public int? QuantityLimitedFrom { get; set; }
}

/// <summary>
/// The guest cart cookie (BR-CART-01): a random 256-bit token the browser keeps for 30 days; the database stores only
/// its SHA-256 (<see cref="Cart.AnonymousTokenHash"/>). HttpOnly, so scripts never see it.
/// </summary>
public static class GuestCartToken
{
    public const string CookieName = "lumen_cart";

    public const int HashLength = 32;

    /// <summary>A new random token for the cookie (base64url, 43 characters).</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>What <see cref="Cart.AnonymousTokenHash"/> stores for a cookie token.</summary>
    public static byte[] Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }
}
