using System.Security.Claims;
using Lumen.Api.Infrastructure.Data;
using Lumen.Domain.Catalog;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Lumen.Api.Carts;

/// <summary>
/// Whose cart a request may see: the signed-in customer's saved cart, or the guest cart named by the cookie's token.
/// Always worked out from the session or the cookie, never from a cart id the client sends, so no request can reach
/// another customer's or another guest's cart.
/// </summary>
internal readonly record struct CartOwner(Guid? CustomerId, string? GuestToken)
{
    public bool IsGuest => CustomerId is null;
}

/// <summary>The outcome of a cart change: the cart as saved (null when nothing needed saving and there is no cart), or why it was refused.</summary>
internal readonly record struct CartEdit(Cart? Cart, CartProblem? Problem);

internal static class CartQueries
{
    public static IQueryable<Cart> WithLines(this IQueryable<Cart> carts) =>
        carts.Include(c => c.Lines).ThenInclude(l => l.Product);

    /// <summary>BR-CART-01: a guest cart counts only while its cookie would still be alive (last change + lifetime).</summary>
    public static IQueryable<Cart> GuestCart(this IQueryable<Cart> carts, byte[] tokenHash, DateTimeOffset aliveAfterUtc) =>
        carts.Where(c => c.CustomerId == null && c.AnonymousTokenHash == tokenHash && c.UpdatedUtc > aliveAfterUtc);

    public static IQueryable<Cart> CustomerCart(this IQueryable<Cart> carts, Guid customerId) =>
        carts.Where(c => c.CustomerId == customerId);

    /// <summary>
    /// Another request changed the same cart first: the cart's xmin moved (two tabs), both created the same customer's
    /// cart or the same product's line (unique indexes), the cart was deleted under a new line (a guest cart merged away
    /// by a sign-in: foreign key), or two merges locked the same rows in opposite order (deadlock). Re-reading and
    /// re-applying the change is safe.
    /// </summary>
    public static bool IsConflict(DbUpdateException exception) =>
        exception is DbUpdateConcurrencyException
        || exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation
                or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure,
        };

    /// <summary>How many times a cart change is re-applied after a conflict before giving up.</summary>
    public const int MaxAttempts = 5;
}

/// <summary>
/// The cart operations behind the cart endpoints (BR-CART-01..03). Every change re-reads the cart, applies the change
/// and saves it with the cart's xmin as the concurrency check (every change touches the cart row), so two tabs
/// changing one cart at once never lose an update: the later one is re-applied on top of the earlier one.
/// </summary>
internal sealed class CartService(
    LumenDbContext db,
    GuestCartCookie cookie,
    AvailabilityPolicy policy,
    TimeProvider time,
    IOptions<CartOptions> options,
    IOptions<IdentityOptions> identity,
    ILogger<CartService> logger)
{
    /// <summary>
    /// A guest who only reads the cart (the mini-cart on every page) keeps it alive too, but the cart row and cookie are
    /// renewed at most once a day, so browsing does not write to the database on every page.
    /// </summary>
    private static readonly TimeSpan GuestRenewalInterval = TimeSpan.FromDays(1);

    private TimeSpan GuestLifetime => options.Value.GuestCartLifetime;

    /// <summary>The caller's cart, read-only, or null when they have none yet (a GET never creates one).</summary>
    public async Task<Cart?> FindAsync(HttpContext http, CancellationToken cancellationToken)
    {
        var owner = OwnerOf(http);
        var now = time.GetUtcNow();
        var cart = await CartOf(owner, now).AsNoTracking().WithLines().SingleOrDefaultAsync(cancellationToken);
        if (owner.IsGuest && owner.GuestToken is { } token)
        {
            if (cart is null)
            {
                // Expired, merged into an account or never ours: drop the dead cookie.
                cookie.Expire(http);
            }
            else if (now - cart.UpdatedUtc >= GuestRenewalInterval)
            {
                await db.Carts.Where(c => c.Id == cart.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedUtc, now), cancellationToken);
                cookie.Write(http, token, now + GuestLifetime);
            }
        }

        return cart;
    }

    /// <summary>BR-CART-03: adds <paramref name="quantity"/> units of a product, to its line if there is one.</summary>
    public Task<CartEdit> AddAsync(HttpContext http, Guid productId, int quantity, CancellationToken cancellationToken) =>
        EditAsync(http, createIfMissing: true, async (cart, now) =>
        {
            var product = await db.Products.FindAsync([productId], cancellationToken);
            if (product is null)
            {
                return CartProblems.ProductNotFound(productId);
            }

            var line = cart.Lines.FirstOrDefault(l => l.ProductId == productId);
            var inCart = line?.Quantity ?? 0;
            if (CartRules.CheckRaise(product, policy, inCart, inCart + quantity) is { } problem)
            {
                return problem;
            }

            if (line is not null)
            {
                // The line keeps the price it was first added at, so an unacknowledged change still shows (BR-CART-02).
                line.Quantity += quantity;
                line.QuantityLimitedFrom = null;
                return null;
            }

            // BR-CART-02: what the customer saw when adding it.
            line = new CartLine
            {
                CartId = cart.Id,
                ProductId = product.Id,
                Product = product,
                Quantity = quantity,
                UnitPriceSeenUsd = CartRules.CurrentUnitPrice(product)!.Value,
                AvailabilitySeen = CartRules.AvailabilityOf(product, policy).Status,
                SellableSeen = product.IsSellable,
                AddedUtc = now,
            };
            cart.Lines.Add(line);
            db.CartLines.Add(line);
            return null;
        }, cancellationToken);

    /// <summary>BR-CART-03: sets a line's quantity. Raising it is checked like an add; lowering it is always allowed.</summary>
    public Task<CartEdit> SetQuantityAsync(HttpContext http, Guid lineId, int quantity, CancellationToken cancellationToken) =>
        EditAsync(http, createIfMissing: false, (cart, _) =>
        {
            var line = cart.Lines.FirstOrDefault(l => l.Id == lineId);
            if (line is null)
            {
                return Task.FromResult<CartProblem?>(CartProblems.LineNotFound(lineId));
            }

            if (quantity > line.Quantity && CartRules.CheckRaise(line.Product!, policy, line.Quantity, quantity) is { } problem)
            {
                return Task.FromResult<CartProblem?>(problem);
            }

            if (quantity != line.Quantity)
            {
                // The customer chose a quantity, so an earlier merge cap no longer applies.
                line.Quantity = quantity;
                line.QuantityLimitedFrom = null;
            }

            return Task.FromResult<CartProblem?>(null);
        }, cancellationToken);

    public Task<CartEdit> RemoveAsync(HttpContext http, Guid lineId, CancellationToken cancellationToken) =>
        EditAsync(http, createIfMissing: false, (cart, _) =>
        {
            var line = cart.Lines.FirstOrDefault(l => l.Id == lineId);
            if (line is null)
            {
                return Task.FromResult<CartProblem?>(CartProblems.LineNotFound(lineId));
            }

            cart.Lines.Remove(line);
            db.CartLines.Remove(line);
            return Task.FromResult<CartProblem?>(null);
        }, cancellationToken);

    /// <summary>BR-CART-02: accepts one line's notices, or every line's when <paramref name="lineId"/> is null.</summary>
    public Task<CartEdit> AcknowledgeAsync(HttpContext http, Guid? lineId, CancellationToken cancellationToken) =>
        EditAsync(http, createIfMissing: false, (cart, now) =>
        {
            var lines = lineId is { } id ? cart.Lines.Where(l => l.Id == id).ToList() : cart.Lines;
            if (lineId is { } missing && lines.Count == 0)
            {
                return Task.FromResult<CartProblem?>(CartProblems.LineNotFound(missing));
            }

            foreach (var line in lines)
            {
                CartRules.Acknowledge(line, line.Product!, policy, now);
            }

            return Task.FromResult<CartProblem?>(null);
        }, cancellationToken);

    /// <summary>
    /// Loads the caller's cart (tracked), applies <paramref name="edit"/> and saves, re-applying the edit on a fresh copy
    /// when another request changed the cart in between. A caller without a cart gets a new, unsaved one: it is saved
    /// only when <paramref name="createIfMissing"/> and the edit put something in it (and then, for a guest, the cookie
    /// is issued). Nothing is saved when the edit returns a problem.
    /// </summary>
    private async Task<CartEdit> EditAsync(
        HttpContext http, bool createIfMissing, Func<Cart, DateTimeOffset, Task<CartProblem?>> edit, CancellationToken cancellationToken)
    {
        var owner = OwnerOf(http);
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            var now = time.GetUtcNow();
            var cart = await CartOf(owner, now).WithLines().SingleOrDefaultAsync(cancellationToken);
            if (cart is null && owner is { IsGuest: true, GuestToken: not null })
            {
                // The cookie names a guest cart that is gone: merged into an account by a sign-in racing this request (in
                // an earlier attempt or before it), or expired. A new guest cart here would be invisible to a browser that
                // is now signed in, so the client re-reads its cart instead; dropping the dead cookie lets a guest retry.
                cookie.Expire(http);
                return new CartEdit(null, CartProblems.CartChanged());
            }

            var isNew = cart is null;
            string? newGuestToken = null;
            if (cart is null)
            {
                newGuestToken = owner.IsGuest ? GuestCartToken.NewToken() : null;
                cart = new Cart
                {
                    CustomerId = owner.CustomerId,
                    AnonymousTokenHash = newGuestToken is null ? null : GuestCartToken.Hash(newGuestToken),
                    CreatedUtc = now,
                    UpdatedUtc = now,
                };
            }

            if (await edit(cart, now) is { } problem)
            {
                db.ChangeTracker.Clear();
                return new CartEdit(null, problem);
            }

            if (isNew)
            {
                if (!createIfMissing || cart.Lines.Count == 0)
                {
                    db.ChangeTracker.Clear();
                    return new CartEdit(null, null);
                }

                db.Carts.Add(cart);
            }
            else
            {
                // Always written, even when only lines changed, so the cart's xmin guards every change.
                cart.UpdatedUtc = now;
                db.Entry(cart).Property(c => c.UpdatedUtc).IsModified = true;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (attempt < CartQueries.MaxAttempts && CartQueries.IsConflict(ex))
            {
                logger.LogDebug(ex, "Cart change conflicted with another request (attempt {Attempt}); re-applying", attempt);
                continue;
            }

            if (owner.IsGuest)
            {
                // Sliding: every change gives the guest cart (and its cookie) another full lifetime.
                cookie.Write(http, newGuestToken ?? owner.GuestToken!, now + GuestLifetime);
            }

            return new CartEdit(cart, null);
        }
    }

    private IQueryable<Cart> CartOf(CartOwner owner, DateTimeOffset now) =>
        owner switch
        {
            { CustomerId: { } customerId } => db.Carts.CustomerCart(customerId),
            { GuestToken: { } token } => db.Carts.GuestCart(GuestCartToken.Hash(token), now - GuestLifetime),
            _ => db.Carts.Where(_ => false),
        };

    private CartOwner OwnerOf(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated != true)
        {
            return new CartOwner(null, GuestCartCookie.Read(http));
        }

        var userId = http.User.FindFirstValue(identity.Value.ClaimsIdentity.UserIdClaimType);
        return Guid.TryParse(userId, out var customerId)
            ? new CartOwner(customerId, null)
            : throw new InvalidOperationException("The signed-in principal has no user id claim.");
    }
}
