using System.Net;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Lumen.IntegrationTests.Carts;

/// <summary>
/// One customer's cart edited from two tabs at the same moment loses no update: every change re-reads the cart and saves
/// it with the cart's xmin as the concurrency check, and a conflict (xmin moved, or both inserted the same cart or line)
/// is re-applied on top of the other change (<c>CartService.MaxAttempts</c>). Two signed-in sessions of one customer
/// stand in for two tabs, and the test makes them collide: it holds a row both saves need, waits until both are blocked
/// on it, then lets go, so the retry path runs every time.
/// </summary>
public sealed class CartConcurrencyTests(LumenApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_tabs_adding_different_products_at_the_same_moment_keep_every_line()
    {
        var (first, second, email) = await TwoTabsOfOneCustomerAsync();
        var products = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => factory.OwnProductAsync()));
        await first.AddOkAsync(products[0].Id, 1);
        var cartId = await factory.WithDbResultAsync(async db =>
        {
            var customerId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(Ct);
            return await db.Carts.Where(c => c.CustomerId == customerId).Select(c => c.Id).SingleAsync(Ct);
        });

        // Both saves update the cart row, so both wait on it; the second to go finds its xmin moved and re-applies.
        var answers = await CollideAsync("""SELECT 1 FROM "Carts" WHERE "Id" = @id FOR UPDATE""", cartId,
            () => first.AddAsync(products[1].Id, 1),
            () => second.AddAsync(products[2].Id, 1));

        answers.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.OK);
        var cart = await first.GetCartAsync();
        cart.Lines.Select(l => l.Product.Id).Should().BeEquivalentTo(products.Select(p => p.Id));
        cart.Lines.Should().AllSatisfy(l => l.Quantity.Should().Be(1));
    }

    [Fact]
    public async Task Two_tabs_adding_the_same_product_before_the_cart_exists_end_with_one_line_of_both_units()
    {
        var (first, second, email) = await TwoTabsOfOneCustomerAsync();
        var product = await factory.OwnProductAsync();
        var customerId = await factory.WithDbResultAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(Ct));

        // Neither tab has a saved cart yet. The first insert takes the unique key on CustomerId, then waits on its
        // foreign key to the held customer row; the second waits on the first's unique key. Once the holder lets go,
        // the first commits and the unique index sends the second back to re-apply onto the cart that now exists.
        var answers = await CollideAsync("""SELECT 1 FROM "AspNetUsers" WHERE "Id" = @id FOR UPDATE""", customerId,
            () => first.AddAsync(product.Id, 1),
            () => second.AddAsync(product.Id, 1));

        answers.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.OK);
        var cart = await second.GetCartAsync();
        cart.Lines.Should().ContainSingle().Which.Quantity.Should().Be(2);
    }

    private async Task<(ApiSession First, ApiSession Second, string Email)> TwoTabsOfOneCustomerAsync()
    {
        var (first, _) = factory.NewBrowser();
        var email = await first.RegisterAsync();
        var (second, _) = factory.NewBrowser();
        await second.SignInAsync(email);
        return (first, second, email);
    }

    /// <summary>
    /// Holds the row <paramref name="lockSql"/> locks, starts <paramref name="requests"/>, waits until each is blocked
    /// on the holder, then commits, so they continue at the same moment (as PlaceOrderTests.RaceAsync does).
    /// </summary>
    private async Task<HttpResponseMessage[]> CollideAsync(string lockSql, Guid id, params Func<Task<HttpResponseMessage>>[] requests)
    {
        await using var holder = new NpgsqlConnection(factory.DatabaseConnectionString);
        await holder.OpenAsync(Ct);
        await using var hold = await holder.BeginTransactionAsync(Ct);
        await using (var lockRow = new NpgsqlCommand(lockSql, holder, hold))
        {
            lockRow.Parameters.AddWithValue("id", id);
            await lockRow.ExecuteNonQueryAsync(Ct);
        }

        var racing = Task.WhenAll(requests.Select(request => request()));

        await using var watcher = new NpgsqlConnection(factory.DatabaseConnectionString);
        await watcher.OpenAsync(Ct);
        // Counted through chains: a second cart insert waits on the first one's unique key, not on the holder itself.
        await using var waiting = new NpgsqlCommand("""
            WITH RECURSIVE blocked(pid) AS (
                SELECT pid FROM pg_stat_activity WHERE @holder = ANY(pg_blocking_pids(pid))
                UNION
                SELECT a.pid FROM pg_stat_activity a JOIN blocked b ON b.pid = ANY(pg_blocking_pids(a.pid)))
            SELECT count(*) FROM blocked
            """, watcher);
        waiting.Parameters.AddWithValue("holder", holder.ProcessID);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((long)(await waiting.ExecuteScalarAsync(Ct))! < requests.Length)
        {
            if (DateTime.UtcNow > deadline || racing.IsCompleted)
            {
                throw new TimeoutException($"The {requests.Length} cart changes never all waited on the held row.");
            }

            await Task.Delay(20, Ct);
        }

        await hold.CommitAsync(Ct);
        return await racing;
    }
}
