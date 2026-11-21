using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;

namespace Lumen.EndToEndTests;

/// <summary>
/// Whole purchases on a running stack (docker compose, or any deployment), over HTTP only: the real API, database,
/// background jobs, payment mock and partner stand-ins. Both tests use the cart of one customer registered for the run,
/// so they live in one class and run one after the other, sharing one signed-in session per account
/// (<see cref="SignedInAccounts"/>).
/// </summary>
public sealed class PurchaseTests(SignedInAccounts accounts) : IClassFixture<SignedInAccounts>
{
    private static readonly object Address = new
    {
        name = "Ada Lovelace", line1 = "1200 NW Naito Pkwy", line2 = (string?)null, city = "Portland", region = "OR", postalCode = "97209", country = "US",
    };

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    [Trait("BR", "BR-PAY-05")]
    public async Task BR_ORD_01_one_cart_from_all_three_sellers_paid_once_by_card_completes_with_every_line_confirmed()
    {
        var test = Stopwatch.StartNew();
        var customer = await accounts.CustomerAsync();
        await customer.EmptyCartAsync();

        // The partner sync runs at start-up; a Kestrel item is sellable once its stock file has been read as well.
        var products = await StackClient.EventuallyAsync(
            async () => await InStockAsync(customer, "lumen") is { } own
                && await InStockAsync(customer, "harbor") is { } harbor
                && await InStockAsync(customer, "kestrel") is { } kestrel
                    ? new[] { own, harbor, kestrel }
                    : null,
            TimeSpan.FromSeconds(90), "a sellable Lumen, Harbor and Kestrel product");
        Log($"catalogue ready after {test.Elapsed.TotalSeconds:0.0} s");

        var order = await PlaceOrderAsync(customer, products);
        var number = order["number"]!.GetValue<string>();
        var total = order["totals"]!["total"]!.GetValue<decimal>();
        order["shipments"]!.AsArray().Select(s => s!["source"]!.GetValue<string>()).Should().Equal("lumen", "harbor", "kestrel");

        // Card 0059: the gateway errors first, then approves the automatic retry of the same payment (BR-PAY-05).
        var paying = Stopwatch.StartNew();
        await customer.PostAsync($"orders/{number}/payments", new
        {
            method = "card",
            card = new { number = "4242000000000059", name = "Ada Lovelace", expiryMonth = 12, expiryYear = DateTime.UtcNow.Year + 2, cvc = "123" },
        }, HttpStatusCode.Accepted);
        var payment = await StackClient.EventuallyAsync(async () =>
        {
            var current = await customer.GetAsync($"orders/{number}/payments/current");
            return current["status"]!.GetValue<string>() is "created" or "processing" or "error" ? null : current;
        }, TimeSpan.FromSeconds(60), $"the payment of {number} to settle");
        payment["status"]!.GetValue<string>().Should().Be("succeeded", "card 0059 is approved on the automatic retry");
        Log($"payment of {total:0.00} succeeded after {paying.Elapsed.TotalSeconds:0.0} s");

        // A background job sends one partner order per partner line once paid; Kestrel decides 10-30 s after receipt.
        var fulfilling = Stopwatch.StartNew();
        var final = await StackClient.EventuallyAsync(async () =>
        {
            var current = await customer.GetAsync($"orders/{number}");
            return Lines(current).All(l => l["status"]!.GetValue<string>() is "confirmed" or "rejected") ? current : null;
        }, TimeSpan.FromSeconds(180), $"every line of {number} to be confirmed or rejected");
        Log($"every line final after {fulfilling.Elapsed.TotalSeconds:0.0} s; whole test {test.Elapsed.TotalSeconds:0.0} s");

        Lines(final).Select(l => l["status"]!.GetValue<string>()).Should().Equal("confirmed", "confirmed", "confirmed");
        final["status"]!.GetValue<string>().Should().Be("completed");
        final["totals"]!["netPaid"]!.GetValue<decimal>().Should().Be(total, "nothing was refunded");

        // Exactly one payment: the admin's view of the order lists every payment event with its payment id.
        var admin = await accounts.AdminAsync();
        var adminOrder = await admin.GetAsync($"admin/orders/{number}");
        var events = adminOrder["paymentEvents"]!.AsArray().Select(e => e!).ToList();
        events.Select(e => e["paymentId"]!.GetValue<string>()).Distinct().Should().Equal(final["payment"]!["paymentId"]!.GetValue<string>());
        events.Select(e => e["type"]!.GetValue<string>()).Should().ContainInOrder("gateway_error", "approved");
        adminOrder["failedPaymentAttempts"]!.GetValue<int>().Should().Be(0);
        Log($"payment timeline: {string.Join(" -> ", events.Select(e => e["type"]!.GetValue<string>()))}");
    }

    [Fact]
    [Trait("BR", "BR-PAY-06")]
    public async Task BR_PAY_06_an_order_paid_from_the_wallet_lowers_the_balance_by_exactly_its_total()
    {
        var customer = await accounts.CustomerAsync();
        await customer.EmptyCartAsync();
        var own = await StackClient.EventuallyAsync(() => InStockAsync(customer, "lumen"), TimeSpan.FromSeconds(90), "a sellable Lumen product");
        var before = (await customer.GetAsync("wallet"))["balance"]!.GetValue<decimal>();

        var order = await PlaceOrderAsync(customer, [own]);
        var number = order["number"]!.GetValue<string>();
        var total = order["totals"]!["total"]!.GetValue<decimal>();
        var payment = await customer.PostAsync($"orders/{number}/payments", new { method = "wallet" }, HttpStatusCode.Accepted);

        payment["status"]!.GetValue<string>().Should().Be("succeeded");
        var wallet = await customer.GetAsync("wallet");
        var after = wallet["balance"]!.GetValue<decimal>();
        after.Should().Be(before - total);
        var newest = wallet["recentTransactions"]!.AsArray()[0]!;
        (newest["kind"]!.GetValue<string>(), newest["amount"]!.GetValue<decimal>(), newest["orderNumber"]!.GetValue<string>())
            .Should().Be(("payment", -total, number));
        Log($"wallet {before:0.00} -> {after:0.00} for order {number} of {total:0.00}");
    }

    /// <summary>The cheapest product of <paramref name="seller"/> that can be bought now, with a few units to spare.</summary>
    private static async Task<string?> InStockAsync(StackClient client, string seller)
    {
        var page = await client.GetAsync($"products?seller={seller}&sort=price_asc&pageSize=96");
        return page["items"]!.AsArray().Select(p => p!)
            .FirstOrDefault(p => p["sellable"]!.GetValue<bool>()
                && p["availability"]!["status"]!.GetValue<string>() == "in_stock"
                && (p["availability"]!["quantity"]?.GetValue<int>() ?? int.MaxValue) >= 3)
            ?["id"]!.GetValue<string>();
    }

    /// <summary>Adds one of each product, reviews the checkout and places exactly the reviewed lines and total.</summary>
    private static async Task<JsonNode> PlaceOrderAsync(StackClient client, IEnumerable<string> productIds)
    {
        foreach (var productId in productIds)
        {
            await client.PostAsync("cart/lines", new { productId, quantity = 1 });
        }

        var preview = await client.GetAsync("checkout/preview?shippingMethod=standard");
        preview["blockers"]!.AsArray().Should().BeEmpty();
        var total = preview["total"]!.GetValue<decimal>();
        var reviewed = preview["shipments"]!.AsArray()
            .SelectMany(s => s!["lines"]!.AsArray())
            .Select(l => new
            {
                productId = l!["productId"]!.GetValue<string>(), quantity = l["quantity"]!.GetValue<int>(), unitPrice = l["unitPrice"]!.GetValue<decimal>(),
            })
            .ToList();

        var order = await client.PostAsync("checkout/orders", new
        {
            checkoutKey = Guid.NewGuid(), lines = reviewed, expectedTotal = total, shippingAddress = Address, shippingMethod = "standard",
        }, HttpStatusCode.Created);
        order["totals"]!["total"]!.GetValue<decimal>().Should().Be(total, "the order charges exactly the reviewed total");
        return order;
    }

    private static IEnumerable<JsonNode> Lines(JsonNode order) =>
        order["shipments"]!.AsArray().SelectMany(s => s!["lines"]!.AsArray()).Select(l => l!);

    private static void Log(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);
}
