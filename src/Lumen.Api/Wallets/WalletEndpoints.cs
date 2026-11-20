using System.Security.Claims;
using Lumen.Api.Identity;
using Lumen.Api.Infrastructure.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Wallets;

/// <summary>The signed-in customer's wallet (BR-PAY-06): balance and recent transactions. Read-only.</summary>
internal static class WalletEndpoints
{
    public static RouteGroupBuilder MapWalletRoutes(this RouteGroupBuilder api)
    {
        api.MapGet("/wallet", GetWallet)
            .WithTags("Wallet")
            .WithName("GetWallet")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .AddEndpointFilter(async (context, next) =>
            {
                // Personal and changes with every payment: never cached.
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                return await next(context);
            });
        return api;
    }

    private static async Task<Results<Ok<WalletDto>, ProblemHttpResult>> GetWallet(
        ClaimsPrincipal user,
        LumenDbContext db,
        WalletService wallets,
        IOptions<WalletOptions> options,
        IOptions<IdentityOptions> identity,
        CancellationToken cancellationToken)
    {
        // The wallet is always the caller's own, from the session; no customer id is ever taken from the request.
        if (!Guid.TryParse(user.FindFirstValue(identity.Value.ClaimsIdentity.UserIdClaimType), out var customerId))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign in to see your wallet");
        }

        // BR-PAY-06: every customer has a wallet, opened on first use if registration could not open it. The customer
        // role decides (it is in the session's claims): staff accounts such as the admin never get mock money.
        if (user.IsInRole(Roles.Customer))
        {
            await wallets.EnsureOpenAsync(customerId, cancellationToken);
        }

        var balance = await db.Wallets.AsNoTracking()
            .Where(w => w.CustomerId == customerId)
            .Select(w => (decimal?)w.Balance)
            .SingleOrDefaultAsync(cancellationToken);
        if (balance is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No wallet",
                detail: "Wallets belong to customer accounts, and this account is not one.");
        }

        var transactions = await db.WalletTransactions.AsNoTracking()
            .Where(t => t.CustomerId == customerId)
            .OrderByDescending(t => t.CreatedUtc).ThenByDescending(t => t.Id)
            .Take(options.Value.RecentTransactionsShown)
            .Select(t => new
            {
                t.Id,
                t.CreatedUtc,
                t.Kind,
                t.Amount,
                t.BalanceAfter,
                OrderNumber = db.Orders.Where(o => o.Id == t.OrderId).Select(o => o.Number).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new WalletDto(
            balance.Value,
            "USD",
            [.. transactions.Select(t => new WalletTransactionDto(t.Id, t.CreatedUtc.UtcDateTime, t.Kind, t.Amount, t.BalanceAfter, t.OrderNumber))]));
    }
}
