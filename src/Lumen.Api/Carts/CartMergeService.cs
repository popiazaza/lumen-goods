using Lumen.Api.Identity;
using Lumen.Api.Infrastructure.Data;
using Lumen.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Carts;

/// <summary>
/// BR-CART-01: on sign-in and registration, the guest cart named by the cookie joins the customer's saved cart and is
/// deleted, in one transaction. Same product: quantities summed, capped at the known available quantity and 10, with a
/// "quantity limited" notice (<see cref="CartRules.MergedQuantity"/>); the saved line keeps its seen price and
/// availability. A product only in the guest cart moves over as it was, notices included, even past
/// <see cref="CartOptions.MaxLines"/> (BR-CART-03): a merge never drops what the customer chose. Such a cart takes no new
/// products, and its checkout is blocked, until lines are removed.
/// Safe to replay and to run twice at once: the guest cart is deleted with its xmin as the concurrency check, so of two
/// merges of the same guest cart only one commits; the other re-reads, finds no guest cart and changes nothing.
/// Safe to call again after a failure: the merge commits in one SaveChanges or not at all, and the cookie is expired
/// only after it committed, so an error (which sign-in logs and moves past) leaves the guest cart for the next sign-in.
/// </summary>
internal sealed class CartMergeService(
    IServiceScopeFactory scopes,
    GuestCartCookie cookie,
    AvailabilityPolicy policy,
    TimeProvider time,
    IOptions<CartOptions> options,
    ILogger<CartMergeService> logger) : ICartMergeService
{
    public async Task MergeGuestCartAsync(HttpContext httpContext, Guid customerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (GuestCartCookie.Read(httpContext) is not { } token)
        {
            return;
        }

        // Its own DbContext: sign-in's context holds Identity's entities, which re-reading the carts must not disturb.
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LumenDbContext>();
        if (!await IsCustomerAsync(db, customerId, cancellationToken))
        {
            // Staff accounts have no cart; the guest cart stays with the browser for when it signs out.
            return;
        }

        var tokenHash = GuestCartToken.Hash(token);
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            var now = time.GetUtcNow();
            var guest = await db.Carts.GuestCart(tokenHash, now - options.Value.GuestCartLifetime).WithLines()
                .SingleOrDefaultAsync(cancellationToken);
            if (guest is null)
            {
                // Already merged (a replayed sign-in), expired, or never existed.
                break;
            }

            // An empty guest cart brings nothing over; it is only deleted.
            var lineCount = guest.Lines.Count;
            if (lineCount > 0)
            {
                var saved = await db.Carts.CustomerCart(customerId).WithLines().SingleOrDefaultAsync(cancellationToken);
                if (saved is null)
                {
                    saved = new Cart { CustomerId = customerId, CreatedUtc = now, UpdatedUtc = now };
                    db.Carts.Add(saved);
                }
                else
                {
                    // Written even when only lines change, so the saved cart's xmin guards the merge too.
                    saved.UpdatedUtc = now;
                    db.Entry(saved).Property(c => c.UpdatedUtc).IsModified = true;
                }

                foreach (var guestLine in guest.Lines.OrderBy(l => l.AddedUtc).ThenBy(l => l.Id))
                {
                    MergeLine(db, saved, guestLine);
                }
            }

            db.Carts.Remove(guest);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Merged a guest cart of {LineCount} lines into customer {CustomerId}'s cart", lineCount, customerId);
                break;
            }
            catch (DbUpdateException ex) when (CartQueries.IsConflict(ex))
            {
                if (attempt >= CartQueries.MaxAttempts)
                {
                    // Signing in must not fail over the cart. The guest cart and its cookie stay, so the next sign-in merges it.
                    logger.LogWarning(ex, "Could not merge the guest cart into customer {CustomerId}'s cart after {Attempts} attempts", customerId, attempt);
                    return;
                }
            }
        }

        cookie.Expire(httpContext);
    }

    /// <summary>Read from the database rather than the caller's principal, so the rule holds whoever calls the merge.</summary>
    private static Task<bool> IsCustomerAsync(LumenDbContext db, Guid userId, CancellationToken cancellationToken) =>
        db.UserRoles.AnyAsync(
            ur => ur.UserId == userId && db.Roles.Any(r => r.Id == ur.RoleId && r.Name == Roles.Customer),
            cancellationToken);

    private void MergeLine(LumenDbContext db, Cart saved, CartLine guestLine)
    {
        var savedLine = saved.Lines.FirstOrDefault(l => l.ProductId == guestLine.ProductId);
        if (savedLine is not null)
        {
            var (quantity, limitedFrom) = CartRules.MergedQuantity(savedLine.Quantity, guestLine.Quantity, guestLine.Product!, policy);
            savedLine.Quantity = quantity;
            savedLine.QuantityLimitedFrom = limitedFrom;
            return;
        }

        var moved = new CartLine
        {
            CartId = saved.Id,
            ProductId = guestLine.ProductId,
            Product = guestLine.Product,
            Quantity = guestLine.Quantity,
            UnitPriceSeenUsd = guestLine.UnitPriceSeenUsd,
            AvailabilitySeen = guestLine.AvailabilitySeen,
            SellableSeen = guestLine.SellableSeen,
            AddedUtc = guestLine.AddedUtc,
            NoticeAcknowledgedUtc = guestLine.NoticeAcknowledgedUtc,
            QuantityLimitedFrom = guestLine.QuantityLimitedFrom,
        };
        saved.Lines.Add(moved);
        db.CartLines.Add(moved);
    }
}
