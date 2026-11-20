using System.Net;
using Lumen.Api.Checkout;
using Lumen.Domain.Orders;
using Lumen.IntegrationTests.Catalog;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Lumen.IntegrationTests.Checkout;

/// <summary>
/// BR-STOCK-01 with many callers: far more customers than units place their orders at the same moment, on the real API
/// and database. The conditional reservation lets exactly as many orders through as there are units.
/// </summary>
[Collection(ManyCallersCollection.Name)]
public sealed class OwnStockRaceTests(CheckoutHost host) : IClassFixture<CheckoutHost>
{
    private const int Customers = 50;
    private const int Units = 7;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("BR", "BR-STOCK-01")]
    public async Task BR_STOCK_01_fifty_customers_ordering_the_last_7_units_at_once_get_exactly_7_orders()
    {
        var product = await host.OwnProductAsync(18.50m, onHand: Units);
        var shoppers = await Task.WhenAll(Enumerable.Range(0, Customers).Select(_ => host.NewShopperAsync()));

        // Every customer reviews the same in-stock product before anyone orders, so each request is one the API accepts
        // on its own; only the race decides who gets a unit.
        var requests = await Task.WhenAll(shoppers.Select(async shopper =>
        {
            await shopper.AddToCartAsync(product, 1);
            return (Shopper: shopper, Request: Shopper.Request(await shopper.PreviewAsync(), Guid.NewGuid()));
        }));

        var race = ManyCallers.AllAtOnceAsync(requests.Select(r => (Func<Task<HttpResponseMessage>>)(() => r.Shopper.PlaceAsync(r.Request))));
        await ManyCallers.CheckWhileRunningAsync(race, async () =>
        {
            var now = await host.ProductAsync(product.Id);
            now.Reserved.Should().BeLessThanOrEqualTo(now.OnHand!.Value, "reserved never exceeds on hand, even mid-race");
        });
        var responses = await race;

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(Units);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(Customers - Units);
        var problems = await Task.WhenAll(responses.Where(r => r.StatusCode == HttpStatusCode.Conflict).Select(r => r.ReadAsync<PriceChangedProblem>()));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{Units} orders created; refused: {string.Join(", ", problems.GroupBy(p => p.Changes.FirstOrDefault()?.Message).Select(g => $"{g.Count()} \"{g.Key}\""))}");
        foreach (var problem in problems)
        {
            var change = problem.Changes.Should().ContainSingle().Subject;

            // The documented out-of-stock answers: the reservation itself refused ("Only 0 left"), or a winner had
            // already committed and the check before it said "Out of stock".
            (change.ProductId, change.Available).Should().Be((product.Id, 0));
            (change.Kind, change.Message).Should().BeOneOf(
                (CheckoutChangeKind.QuantityLimited, "Only 0 left"), (CheckoutChangeKind.Unavailable, "Out of stock"));
            problem.CanConfirm.Should().BeFalse();
        }

        var after = await host.ProductAsync(product.Id);
        (after.OnHand, after.Reserved).Should().Be((Units, Units));
        var orders = await host.WithDbAsync(db => db.Orders.AsNoTracking()
            .Where(o => o.Lines.Any(l => l.ProductId == product.Id))
            .Select(o => new { o.CustomerId, o.Status, Units = o.Lines.Where(l => l.ProductId == product.Id).Sum(l => l.Quantity) })
            .ToListAsync(Ct));
        orders.Should().HaveCount(Units).And.OnlyContain(o => o.Units == 1 && o.Status == OrderStatus.AwaitingPayment);
        orders.Select(o => o.CustomerId).Should().OnlyHaveUniqueItems("each winner is a different customer");
    }
}
