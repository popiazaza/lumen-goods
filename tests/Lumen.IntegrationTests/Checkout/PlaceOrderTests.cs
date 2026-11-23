using System.Net;
using Lumen.Api.Catalog;
using Lumen.Api.Checkout;
using Lumen.Api.Orders;
using Lumen.Api.Partners;
using Lumen.Domain.Catalog;
using Lumen.Domain.Orders;
using Lumen.Domain.Pricing;
using Lumen.IntegrationTests.Carts;
using Lumen.IntegrationTests.Catalog;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Lumen.IntegrationTests.Checkout;

/// <summary>
/// The checkout preview and Place order on the real API and database, with the partners' live checks stubbed at HTTP
/// (BR-CHK-01, BR-CHK-02, BR-STOCK-01, BR-PAY-01 (a), BR-SHIP-01).
/// </summary>
public sealed class PlaceOrderTests(CheckoutHost host) : IClassFixture<CheckoutHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("BR", "BR-SHIP-01")]
    [Trait("BR", "BR-MONEY-02")]
    public async Task BR_SHIP_01_the_preview_prices_each_sellers_shipment_with_per_line_tax()
    {
        var shopper = await host.NewShopperAsync();
        var mug = await host.OwnProductAsync(18.50m);
        var socks = await host.KestrelProductAsync(14.95m);
        await shopper.AddToCartAsync(mug, 2);
        await shopper.AddToCartAsync(socks, 1);

        var preview = await shopper.PreviewAsync("express");

        preview.Shipments.Select(s => (s.Source, s.Method, s.Fee)).Should().Equal(
            ("lumen", ShippingMethod.Express, 12.99m), ("kestrel", ShippingMethod.Standard, 4.99m));
        preview.Shipments[0].Lines.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { ProductId = mug.Id, UnitPrice = 18.50m, Quantity = 2, LineTotal = 37.00m, Tax = 2.96m });
        preview.Shipments[1].Lines.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { ProductId = socks.Id, UnitPrice = 16.22m, Quantity = 1, LineTotal = 16.22m, Tax = 1.30m });
        (preview.Subtotal, preview.Shipping, preview.Tax, preview.Total).Should().Be((53.22m, 17.98m, 4.26m, 75.46m));
        preview.ShippingOptions.Select(o => (o.Method, o.Fee, o.ShippingTotal)).Should().Equal(
            (ShippingMethod.Standard, 4.99m, 9.98m), (ShippingMethod.Express, 12.99m, 17.98m));
        preview.Blockers.Should().BeEmpty();
    }

    [Fact]
    [Trait("BR", "BR-SHIP-01")]
    public async Task BR_SHIP_01_express_is_not_offered_for_partner_items_only()
    {
        var shopper = await host.NewShopperAsync();
        await shopper.AddToCartAsync(await host.KestrelProductAsync(), 1);

        (await shopper.PreviewAsync()).ShippingOptions.Select(o => o.Method).Should().Equal(ShippingMethod.Standard);
        var express = await shopper.Session.GetAsync("/api/v1/checkout/preview?shippingMethod=express");
        express.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    [Trait("BR", "BR-CHK-01")]
    [Trait("BR", "BR-STOCK-01")]
    public async Task BR_CHK_01_place_order_creates_the_order_with_reserved_stock_and_a_15_minute_hold()
    {
        var shopper = await host.NewShopperAsync();
        var mug = await host.OwnProductAsync(18.50m, onHand: 5);
        var socks = await host.KestrelProductAsync(14.95m);
        await shopper.AddToCartAsync(mug, 2);
        await shopper.AddToCartAsync(socks, 1);
        var preview = await shopper.PreviewAsync();

        var response = await shopper.PlaceAsync(preview);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var order = await response.ReadAsync<OrderDetailDto>();
        response.Headers.Location!.ToString().Should().Be($"/api/v1/orders/{order.Number}");
        order.Status.Should().Be(OrderStatus.AwaitingPayment);
        order.Totals.Total.Should().Be(preview.Total);
        order.HoldExpiresUtc.Should().Be(host.Clock.GetUtcNow().AddMinutes(15).UtcDateTime);
        order.CanPay.Should().BeTrue();

        (await host.ProductAsync(mug.Id)).Reserved.Should().Be(2);
        var saved = await host.OrderAsync(order.Number);
        var kestrelLine = saved.Lines.Single(l => l.ProductId == socks.Id);
        (kestrelLine.UnitPriceUsd, kestrelLine.SourceUnitPrice, kestrelLine.SourceCurrency, kestrelLine.FxRateToUsd)
            .Should().Be((16.22m, 14.95m, "EUR", CheckoutHost.EurToUsd));
        saved.Lines.OrderBy(l => l.LineNumber).Select(l => l.ProductId).Should().Equal(mug.Id, socks.Id);
        saved.Lines.Should().OnlyContain(l => l.Status == OrderLineStatus.Pending);
    }

    [Theory]
    [Trait("BR", "BR-PAY-01")]
    [InlineData(10)] // enough stock for both: the unique checkout key refuses the second order
    [InlineData(3)] // only enough for one: the second cannot reserve, and finds the first's order
    public async Task BR_PAY_01_a_double_clicked_place_order_creates_one_order_and_both_get_it(int onHand)
    {
        var shopper = await host.NewShopperAsync();
        var mug = await host.OwnProductAsync(onHand: onHand);
        await shopper.AddToCartAsync(mug, 3);
        var preview = await shopper.PreviewAsync();
        var key = Guid.NewGuid();

        var responses = await Task.WhenAll(shopper.PlaceAsync(preview, key), shopper.PlaceAsync(preview, key));

        responses.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.Created, HttpStatusCode.OK]);
        var numbers = await Task.WhenAll(responses.Select(async r => (await r.ReadAsync<OrderDetailDto>()).Number));
        numbers.Distinct().Should().ContainSingle();
        (await host.WithDbAsync(db => db.Orders.CountAsync(o => o.CheckoutKey == key, Ct))).Should().Be(1);
        (await host.ProductAsync(mug.Id)).Reserved.Should().Be(3, "the stock is reserved once");

        var again = await shopper.PlaceAsync(preview, key);
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await again.ReadAsync<OrderDetailDto>()).Number.Should().Be(numbers[0]);
    }

    [Fact]
    [Trait("BR", "BR-STOCK-01")]
    public async Task BR_STOCK_01_two_customers_racing_for_the_last_unit_get_one_order_and_one_409()
    {
        var lastUnit = await host.OwnProductAsync(onHand: 1);
        var ada = await host.NewShopperAsync();
        var bob = await host.NewShopperAsync();
        await ada.AddToCartAsync(lastUnit, 1);
        await bob.AddToCartAsync(lastUnit, 1);
        var adaPreview = await ada.PreviewAsync();
        var bobPreview = await bob.PreviewAsync();

        var responses = await Task.WhenAll(ada.PlaceAsync(adaPreview), bob.PlaceAsync(bobPreview));

        responses.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.Created, HttpStatusCode.Conflict]);
        var problem = await responses.Single(r => r.StatusCode == HttpStatusCode.Conflict).ReadAsync<PriceChangedProblem>();
        var change = problem.Changes.Should().ContainSingle().Subject;
        (change.ProductId, change.Available).Should().Be((lastUnit.Id, 0));

        // Which check refuses the loser depends on timing, and both are right: its own reservation ("Only 0 left"), or,
        // when the winner had already committed, the check before it ("Out of stock").
        (change.Kind, change.Message).Should().BeOneOf(
            (CheckoutChangeKind.QuantityLimited, "Only 0 left"), (CheckoutChangeKind.Unavailable, "Out of stock"));
        problem.CanConfirm.Should().BeFalse();
        var product = await host.ProductAsync(lastUnit.Id);
        (product.OnHand, product.Reserved).Should().Be((1, 1));
    }

    [Fact]
    [Trait("BR", "BR-CHK-01")]
    public async Task BR_CHK_01_a_price_rise_after_review_returns_409_with_old_and_new_price_and_creates_nothing()
    {
        var shopper = await host.NewShopperAsync();
        var socks = await host.KestrelProductAsync(14.95m);
        var tote = await host.HarborProductAsync(49.90m);
        await shopper.AddToCartAsync(socks, 1);
        await shopper.AddToCartAsync(tote, 1);
        var preview = await shopper.PreviewAsync();
        preview.Total.Should().Be(81.39m);

        // A Kestrel sync moves 14,95 EUR to 15,95 EUR between the review and Place order.
        await host.WithDbAsync(db => db.Products.Where(p => p.Id == socks.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.SourcePrice, 15.95m).SetProperty(p => p.PriceUsd, 17.31m), Ct));
        var key = Guid.NewGuid();
        var response = await shopper.PlaceAsync(preview, key);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.ReadAsync<PriceChangedProblem>();
        problem.Changes.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            ProductId = socks.Id, Kind = CheckoutChangeKind.PriceChanged, From = 16.22m, To = 17.31m, Message = "Price changed from $16.22 to $17.31",
        });
        (problem.NewTotal, problem.CanConfirm).Should().Be((82.56m, true));
        (await host.WithDbAsync(db => db.Orders.AnyAsync(o => o.CheckoutKey == key, Ct))).Should().BeFalse("nothing is created");

        // The customer confirms the new amounts with the same key.
        var confirmed = Shopper.Request(preview, key) with
        {
            Lines = [new PlaceOrderLineDto(socks.Id, 1, 17.31m), new PlaceOrderLineDto(tote.Id, 1, 49.90m)],
            ExpectedTotal = problem.NewTotal!.Value,
        };
        var placed = await shopper.PlaceAsync(confirmed);
        placed.StatusCode.Should().Be(HttpStatusCode.Created);
        (await placed.ReadAsync<OrderDetailDto>()).Totals.Total.Should().Be(82.56m);
    }

    [Fact]
    [Trait("BR", "BR-CHK-01")]
    public async Task BR_CHK_01_a_live_price_drop_also_returns_409_is_saved_to_the_product_and_converges()
    {
        var shopper = await host.NewShopperAsync();
        var tote = await host.HarborProductAsync(49.90m);
        await shopper.AddToCartAsync(tote, 2);
        var preview = await shopper.PreviewAsync();
        host.Harbor.Set(tote.SourceProductId, 4490);

        var response = await shopper.PlaceAsync(preview);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.ReadAsync<PriceChangedProblem>();
        problem.Changes.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Kind = CheckoutChangeKind.PriceChanged, From = 49.90m, To = 44.90m });
        problem.CanConfirm.Should().BeTrue();
        (await host.ProductAsync(tote.Id)).PriceUsd.Should().Be(44.90m, "the live price is written through");

        var again = await shopper.PreviewAsync();
        again.Total.Should().Be(problem.NewTotal);
        var placed = await shopper.PlaceAsync(again);
        placed.StatusCode.Should().Be(HttpStatusCode.Created);
        (await placed.ReadAsync<OrderDetailDto>()).Shipments.Single().Lines.Single().UnitPrice.Should().Be(44.90m);
    }

    [Fact]
    [Trait("BR", "BR-CAT-02")]
    public async Task BR_CAT_02_a_live_harbor_price_above_999_999_99_refuses_the_line_and_the_write_through_makes_it_not_sellable()
    {
        // Saved as the live price, $1,000,000.00 a unit would let 50 lines of 10 overflow the order totals.
        var shopper = await host.NewShopperAsync();
        var tote = await host.HarborProductAsync(49.90m);
        await shopper.AddToCartAsync(tote, 1);
        var preview = await shopper.PreviewAsync();
        host.Harbor.Set(tote.SourceProductId, 100_000_000);

        var response = await shopper.PlaceAsync(preview);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.ReadAsync<PriceChangedProblem>();
        problem.Changes.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Kind = CheckoutChangeKind.Unavailable, Message = "No longer available" });
        problem.NewTotal.Should().BeNull();
        var saved = await host.ProductAsync(tote.Id);
        (saved.IsActive, saved.PriceUsd).Should().Be((false, 49.90m), "the price above the cap is never stored, and the product is not sellable");
        (await host.WithDbAsync(db => db.Orders.CountAsync(o => o.CustomerId == shopper.Id, Ct))).Should().Be(0);
    }

    [Fact]
    [Trait("BR", "BR-CHK-02")]
    public async Task BR_CHK_02_a_partner_line_short_of_stock_returns_409_only_n_available_and_creates_nothing()
    {
        var shopper = await host.NewShopperAsync();
        var socks = await host.KestrelProductAsync(stock: 5);
        await shopper.AddToCartAsync(socks, 2);
        var preview = await shopper.PreviewAsync();
        host.Kestrel.Stock[socks.SourceProductId] = 1;
        var key = Guid.NewGuid();

        var response = await shopper.PlaceAsync(preview, key);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.ReadAsync<PriceChangedProblem>();
        problem.Changes.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            ProductId = socks.Id, SellerName = "Kestrel Outfitters", Kind = CheckoutChangeKind.QuantityLimited, Available = 1, Message = "Only 1 available",
        });
        (problem.NewTotal, problem.CanConfirm).Should().Be(((decimal?)null, false));
        (await host.WithDbAsync(db => db.Orders.AnyAsync(o => o.CheckoutKey == key, Ct))).Should().BeFalse();
        (await host.ProductAsync(socks.Id)).Quantity.Should().Be(1, "the live stock is written through");
    }

    [Fact]
    [Trait("BR", "BR-CHK-02")]
    public async Task BR_CHK_02_an_unreachable_partner_line_is_temporarily_unavailable_and_the_rest_can_be_bought()
    {
        var shopper = await host.NewShopperAsync();
        var mug = await host.OwnProductAsync();
        var tote = await host.HarborProductAsync();
        await shopper.AddToCartAsync(mug, 1);
        await shopper.AddToCartAsync(tote, 1);
        var preview = await shopper.PreviewAsync();

        host.Harbor.Down = true;
        HttpResponseMessage response;
        try
        {
            response = await shopper.PlaceAsync(preview);
        }
        finally
        {
            host.Harbor.Down = false;
        }

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.ReadAsync<PriceChangedProblem>();
        problem.Changes.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            ProductId = tote.Id, SellerName = "Harbor Supply Co.", Kind = CheckoutChangeKind.TemporarilyUnavailable, Message = "Temporarily unavailable",
        });

        await host.WithDbAsync(db => db.CartLines.Where(l => l.ProductId == tote.Id && db.Carts.Any(c => c.Id == l.CartId && c.CustomerId == shopper.Id))
            .ExecuteDeleteAsync(Ct));
        var placed = await shopper.PlaceAsync(await shopper.PreviewAsync());
        placed.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    [Trait("BR", "BR-CHK-02")]
    public async Task BR_CHK_02_harbor_lines_beyond_the_live_checks_are_ordered_on_a_fresh_sync_and_temporarily_unavailable_on_a_stale_one()
    {
        // Harbor's rate limit fits 6 live checks (Partners:Harbor:MaxLiveChecks). Six lines with a known quantity are
        // asked live; the seventh, AVAILABLE with no number, ranks last and is not asked at all.
        var shopper = await host.NewShopperAsync();
        var known = new List<Product>();
        for (var i = 0; i < 6; i++)
        {
            known.Add(await host.HarborProductAsync(onHand: 20));
        }

        var plenty = await host.HarborProductAsync();
        foreach (var product in known.Append(plenty))
        {
            await shopper.AddToCartAsync(product, 1);
        }

        var preview = await shopper.PreviewAsync();
        var before = await host.WithDbAsync(db => db.PartnerSyncStates.AsNoTracking()
            .Where(s => s.Partner == ProductSources.Harbor).Select(s => s.LastSuccessUtc).SingleOrDefaultAsync(Ct));
        try
        {
            await SetHarborLastSyncAsync(host.Clock.GetUtcNow() - TimeSpan.FromMinutes(5));
            var stale = await shopper.PlaceAsync(preview);

            stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await stale.ReadAsync<PriceChangedProblem>()).Changes.Should().ContainSingle().Which.Should().BeEquivalentTo(new
            {
                ProductId = plenty.Id, Kind = CheckoutChangeKind.TemporarilyUnavailable,
            });
            host.Harbor.Calls.Should().NotContain(uri => uri.AbsolutePath.EndsWith(plenty.SourceProductId, StringComparison.Ordinal),
                "a line beyond the live checks is never sent");

            await SetHarborLastSyncAsync(host.Clock.GetUtcNow() - TimeSpan.FromSeconds(30));
            var placed = await shopper.PlaceAsync(await shopper.PreviewAsync());

            placed.StatusCode.Should().Be(HttpStatusCode.Created, await placed.Content.ReadAsStringAsync(Ct));
            (await placed.ReadAsync<OrderDetailDto>()).Shipments.SelectMany(s => s.Lines).Should().HaveCount(7);
        }
        finally
        {
            // The partner sync state is shared by every test in the assembly: leave it as it was.
            await SetHarborLastSyncAsync(before);
        }
    }

    private Task SetHarborLastSyncAsync(DateTimeOffset? at) => host.WithDbAsync(async db =>
    {
        var state = await db.PartnerSyncStates.SingleOrDefaultAsync(s => s.Partner == ProductSources.Harbor, Ct);
        if (state is null)
        {
            state = new PartnerSyncState { Partner = ProductSources.Harbor };
            db.PartnerSyncStates.Add(state);
        }

        state.LastSuccessUtc = at;
        await db.SaveChangesAsync(Ct);
        return 0;
    });

    [Fact]
    [Trait("BR", "BR-CHK-02")]
    public async Task BR_CHK_02_a_partner_that_does_not_answer_within_5_seconds_is_temporarily_unavailable()
    {
        var shopper = await host.NewShopperAsync();
        var tote = await host.HarborProductAsync();
        await shopper.AddToCartAsync(tote, 1);
        var preview = await shopper.PreviewAsync();

        host.Harbor.Hang = true;
        host.Harbor.ExpectCall();
        HttpResponseMessage response;
        try
        {
            var placing = shopper.PlaceAsync(preview);
            await host.Harbor.Called.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            host.Clock.Advance(TimeSpan.FromSeconds(5));
            response = await placing.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
        finally
        {
            host.Harbor.Hang = false;
        }

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.ReadAsync<PriceChangedProblem>()).Changes.Should().ContainSingle()
            .Which.Kind.Should().Be(CheckoutChangeKind.TemporarilyUnavailable);
    }

    [Fact]
    [Trait("BR", "BR-CHK-01")]
    public async Task BR_CHK_01_a_cart_changed_in_another_tab_returns_409_naming_the_added_removed_and_changed_lines()
    {
        var shopper = await host.NewShopperAsync();
        var mug = await host.OwnProductAsync();
        var tee = await host.OwnProductAsync(28.00m);
        await shopper.AddToCartAsync(mug, 1);
        await shopper.AddToCartAsync(tee, 1);
        var preview = await shopper.PreviewAsync();

        // Another tab: the tee is removed, the mug goes to 3, a cap is added.
        var cap = await host.OwnProductAsync(12.00m);
        await host.WithDbAsync(async db =>
        {
            await db.CartLines.Where(l => l.ProductId == tee.Id && db.Carts.Any(c => c.Id == l.CartId && c.CustomerId == shopper.Id)).ExecuteDeleteAsync(Ct);
            return await db.CartLines.Where(l => l.ProductId == mug.Id && db.Carts.Any(c => c.Id == l.CartId && c.CustomerId == shopper.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Quantity, 3), Ct);
        });
        await shopper.AddToCartAsync(cap, 1);

        var response = await shopper.PlaceAsync(preview);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.ReadAsync<PriceChangedProblem>();
        problem.Changes.Select(c => (c.ProductId, c.Kind, c.QuantityFrom, c.QuantityTo)).Should().BeEquivalentTo(new[]
        {
            (mug.Id, CheckoutChangeKind.QuantityChanged, (int?)1, (int?)3),
            (cap.Id, CheckoutChangeKind.CartChanged, 0, 1),
            (tee.Id, CheckoutChangeKind.CartChanged, 1, 0),
        });
        problem.Changes.Single(c => c.ProductId == tee.Id).CartLineId.Should().BeNull();
        (problem.NewTotal, problem.CanConfirm).Should().Be(((decimal?)null, false));
    }

    [Fact]
    [Trait("BR", "BR-CHK-01")]
    public async Task BR_CHK_01_a_different_reviewed_total_alone_returns_409_with_the_new_total()
    {
        var shopper = await host.NewShopperAsync();
        await shopper.AddToCartAsync(await host.OwnProductAsync(10.00m), 1);
        var preview = await shopper.PreviewAsync();

        var response = await shopper.PlaceAsync(Shopper.Request(preview, Guid.NewGuid()) with { ExpectedTotal = preview.Total - 1m });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.ReadAsync<PriceChangedProblem>();
        (problem.Changes.Count, problem.NewTotal, problem.CanConfirm).Should().Be((0, preview.Total, true));
    }

    [Fact]
    [Trait("BR", "BR-CHK-01")]
    public async Task BR_CHK_01_a_place_order_without_the_reviewed_total_is_a_validation_error_not_a_409()
    {
        var shopper = await host.NewShopperAsync();
        await shopper.AddToCartAsync(await host.OwnProductAsync(10.00m), 1);
        var preview = await shopper.PreviewAsync();
        var line = preview.Shipments.Single().Lines.Single();
        var body = new
        {
            checkoutKey = Guid.NewGuid(),
            lines = new[] { new { productId = line.ProductId, quantity = line.Quantity, unitPrice = line.UnitPrice } },
            shippingAddress = CheckoutHost.Address,
            shippingMethod = "standard",
        };

        var response = await CheckoutHost.SendJsonAsync(shopper.Session, HttpMethod.Post, "/api/v1/checkout/orders", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().Equal("expectedTotal");
    }

    [Fact]
    [Trait("BR", "BR-CHK-03")]
    [Trait("BR", "BR-PAY-01")]
    public async Task BR_CHK_03_a_fourth_unpaid_order_is_refused_until_one_is_paid_and_a_repeated_key_still_gets_its_order()
    {
        var shopper = await host.NewShopperAsync();
        var mug = await host.OwnProductAsync(10.00m, onHand: 10);
        await shopper.AddToCartAsync(mug, 1);
        var preview = await shopper.PreviewAsync();
        var keys = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var unpaid = new List<OrderDetailDto>();
        foreach (var key in keys)
        {
            var placed = await shopper.PlaceAsync(preview, key);
            placed.StatusCode.Should().Be(HttpStatusCode.Created);
            unpaid.Add(await placed.ReadAsync<OrderDetailDto>());
        }

        var fourth = await shopper.PlaceAsync(preview);
        var repeated = await shopper.PlaceAsync(preview, keys[2]);

        fourth.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await fourth.ReadJsonAsync();
        (problem.GetProperty("type").GetString(), problem.GetProperty("detail").GetString()).Should().Be((
            CheckoutProblemTypes.TooManyUnpaidOrders,
            "You already have 3 unpaid orders. Pay for one of them in Your orders, or wait until one expires, then place this order again. Nothing was created."));
        (await host.ProductAsync(mug.Id)).Reserved.Should().Be(3, "the refused order reserved nothing");
        repeated.StatusCode.Should().Be(HttpStatusCode.OK, "a repeated checkout key gets its order, even at the limit");
        (await repeated.ReadAsync<OrderDetailDto>()).Number.Should().Be(unpaid[2].Number);

        (await shopper.PayByWalletAsync(unpaid[0].Number)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await shopper.AddToCartAsync(mug, 1); // paying took the mug out of the cart
        (await shopper.PlaceAsync(await shopper.PreviewAsync())).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    [Trait("BR", "BR-CHK-03")]
    public async Task BR_CHK_03_place_orders_racing_for_the_last_unpaid_slot_create_exactly_one_order()
    {
        var (shopper, mug, preview) = await ShopperWithTwoUnpaidOrdersAsync();

        var racing = await RaceAsync(mug, 4, () => shopper.PlaceAsync(preview));

        racing.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        foreach (var refused in racing.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await refused.ReadJsonAsync()).GetProperty("type").GetString().Should().Be(CheckoutProblemTypes.TooManyUnpaidOrders);
        }

        (await host.WithDbAsync(db => db.Orders.CountAsync(o => o.CustomerId == shopper.Id, Ct))).Should().Be(3);
        (await host.ProductAsync(mug.Id)).Reserved.Should().Be(3);
    }

    [Fact]
    [Trait("BR", "BR-CHK-03")]
    [Trait("BR", "BR-PAY-01")]
    public async Task BR_CHK_03_a_double_click_that_fills_the_last_unpaid_slot_gets_the_same_order_twice()
    {
        var (shopper, mug, preview) = await ShopperWithTwoUnpaidOrdersAsync();
        var key = Guid.NewGuid();

        var responses = await RaceAsync(mug, 2, () => shopper.PlaceAsync(preview, key));

        responses.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.Created, HttpStatusCode.OK]);
        var numbers = await Task.WhenAll(responses.Select(async r => (await r.ReadAsync<OrderDetailDto>()).Number));
        numbers.Distinct().Should().ContainSingle();
    }

    [Fact]
    [Trait("BR", "BR-CHK-03")]
    public async Task BR_CHK_03_an_order_whose_hold_ended_no_longer_counts_but_one_taking_a_payment_still_does()
    {
        var (shopper, _, preview) = await ShopperWithTwoUnpaidOrdersAsync();
        (await shopper.PlaceAsync(preview)).StatusCode.Should().Be(HttpStatusCode.Created);
        var processing = await host.WithDbAsync(db => db.Orders.Where(o => o.CustomerId == shopper.Id).Select(o => o.Id).Take(2).ToListAsync(Ct));
        await host.WithDbAsync(db => db.Orders.Where(o => processing.Contains(o.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.PaymentProcessing), Ct));

        // Every hold ends; the payment window job (off in tests) has not expired the order still awaiting payment.
        host.Clock.Advance(TimeSpan.FromMinutes(15));
        var fourth = await shopper.PlaceAsync(preview);
        var fifth = await shopper.PlaceAsync(preview);

        fourth.StatusCode.Should().Be(HttpStatusCode.Created, "only the two orders taking a payment count");
        fifth.StatusCode.Should().Be(HttpStatusCode.Conflict, "the two payments in progress and the new order make 3, past their holds or not");
        (await fifth.ReadJsonAsync()).GetProperty("type").GetString().Should().Be(CheckoutProblemTypes.TooManyUnpaidOrders);
    }

    [Fact]
    [Trait("BR", "BR-CHK-03")]
    [Trait("BR", "BR-PAY-01")]
    public async Task BR_CHK_03_a_repeat_whose_first_request_fills_the_last_slot_after_the_key_lookup_still_gets_that_order()
    {
        var (shopper, _, preview) = await ShopperWithTwoUnpaidOrdersAsync();
        var key = Guid.NewGuid();
        HttpResponseMessage? first = null;
        // The repeat has looked its key up and found nothing; before it counts, the first request with that key commits
        // the customer's third order.
        host.Queries.BeforeNext(QueryHook.IsUnpaidCount, async _ => first = await shopper.PlaceAsync(preview, key));

        var repeat = await shopper.PlaceAsync(preview, key);

        first!.StatusCode.Should().Be(HttpStatusCode.Created);
        repeat.StatusCode.Should().Be(HttpStatusCode.OK, "a repeated checkout key gets its order, also when that order filled the limit");
        (await repeat.ReadAsync<OrderDetailDto>()).Number.Should().Be((await first.ReadAsync<OrderDetailDto>()).Number);
    }

    /// <summary>
    /// Makes <paramref name="requests"/> Place orders truly overlap: the test holds <paramref name="product"/>'s row, so
    /// every request that gets as far as reserving it waits, and so does every request queued behind one of those. Once
    /// all of them wait (directly or not) on the test, it lets go and they finish in whatever order the database decides.
    /// </summary>
    private async Task<HttpResponseMessage[]> RaceAsync(Product product, int requests, Func<Task<HttpResponseMessage>> place)
    {
        var connectionString = await host.WithDbAsync(db => Task.FromResult(db.Database.GetConnectionString()!));
        await using var holder = new NpgsqlConnection(connectionString);
        await holder.OpenAsync(Ct);
        await using var hold = await holder.BeginTransactionAsync(Ct);
        await using (var lockRow = new NpgsqlCommand("""SELECT 1 FROM "Products" WHERE "Id" = @id FOR UPDATE""", holder, hold))
        {
            lockRow.Parameters.AddWithValue("id", product.Id);
            await lockRow.ExecuteNonQueryAsync(Ct);
        }

        var racing = Task.WhenAll(Enumerable.Range(0, requests).Select(_ => place()));

        await using var watcher = new NpgsqlConnection(connectionString);
        await watcher.OpenAsync(Ct);
        await using var waiting = new NpgsqlCommand("""
            WITH RECURSIVE blocked(pid) AS (
                SELECT pid FROM pg_stat_activity WHERE @holder = ANY(pg_blocking_pids(pid))
                UNION
                SELECT a.pid FROM pg_stat_activity a JOIN blocked b ON b.pid = ANY(pg_blocking_pids(a.pid)))
            SELECT count(*) FROM blocked
            """, watcher);
        waiting.Parameters.AddWithValue("holder", holder.ProcessID);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((long)(await waiting.ExecuteScalarAsync(Ct))! < requests)
        {
            if (DateTime.UtcNow > deadline || racing.IsCompleted)
            {
                throw new TimeoutException($"The {requests} Place orders never all waited on the held row.");
            }

            await Task.Delay(20, Ct);
        }

        await hold.CommitAsync(Ct);
        return await racing;
    }

    private async Task<(Shopper Shopper, Product Mug, CheckoutPreviewDto Preview)> ShopperWithTwoUnpaidOrdersAsync()
    {
        var shopper = await host.NewShopperAsync();
        var mug = await host.OwnProductAsync(10.00m, onHand: 10);
        await shopper.AddToCartAsync(mug, 1);
        var preview = await shopper.PreviewAsync();
        for (var i = 0; i < 2; i++)
        {
            (await shopper.PlaceAsync(preview)).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        return (shopper, mug, preview);
    }

    [Fact]
    public async Task Place_order_checks_the_us_address_and_needs_a_cart()
    {
        var shopper = await host.NewShopperAsync();
        var empty = await shopper.PlaceAsync(new PlaceOrderRequest(Guid.NewGuid(), [new PlaceOrderLineDto(Guid.NewGuid(), 1, 1m)], 1m, CheckoutHost.Address));
        empty.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        await shopper.AddToCartAsync(await host.OwnProductAsync(), 1);
        var preview = await shopper.PreviewAsync();
        var bad = await shopper.PlaceAsync(Shopper.Request(preview, Guid.NewGuid()) with
        {
            ShippingAddress = CheckoutHost.Address with { Region = "XX", PostalCode = "9720" },
        });

        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await bad.ReadJsonAsync();
        body.GetProperty("errors").EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("shippingAddress.region", "shippingAddress.postalCode");
    }

    [Fact]
    public async Task Place_order_without_a_key_or_lines_or_with_a_quantity_out_of_range_is_a_validation_error()
    {
        var shopper = await host.NewShopperAsync();
        await shopper.AddToCartAsync(await host.OwnProductAsync(), 1);
        var preview = await shopper.PreviewAsync();
        var line = preview.Shipments.Single().Lines.Single();
        object[] Lines(int quantity) => [new { productId = line.ProductId, quantity, unitPrice = line.UnitPrice }];
        Task<HttpResponseMessage> PlaceAsync(Guid? checkoutKey, object[]? lines) =>
            CheckoutHost.SendJsonAsync(shopper.Session, HttpMethod.Post, "/api/v1/checkout/orders", new
            {
                checkoutKey, lines, expectedTotal = preview.Total, shippingAddress = CheckoutHost.Address, shippingMethod = "standard",
            });

        // The built-in validation answers these before the handler runs (attributes on PlaceOrderRequest).
        HttpResponseMessage[] responses =
        [
            await PlaceAsync(null, Lines(1)),
            await PlaceAsync(Guid.NewGuid(), null),
            await PlaceAsync(Guid.NewGuid(), []),
            await PlaceAsync(Guid.NewGuid(), Lines(0)),
            await PlaceAsync(Guid.NewGuid(), Lines(11)),
        ];

        responses.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.BadRequest);
        (await host.WithDbAsync(db => db.Orders.CountAsync(o => o.CustomerId == shopper.Id, Ct))).Should().Be(0);
    }

    [Fact]
    [Trait("BR", "BR-CART-03")]
    public async Task BR_CART_03_place_order_with_more_than_50_lines_is_a_validation_error_naming_lines()
    {
        var shopper = await host.NewShopperAsync();
        await shopper.AddToCartAsync(await host.OwnProductAsync(), 1);
        var lines = Enumerable.Range(0, PlaceOrderRequest.MaxLines + 1).Select(_ => new PlaceOrderLineDto(Guid.NewGuid(), 1, 10.00m)).ToList();

        var response = await shopper.PlaceAsync(new PlaceOrderRequest(Guid.NewGuid(), lines, 100.00m, CheckoutHost.Address));

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest);
        problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().Equal("lines");
        problem.GetProperty("errors").GetProperty("lines")[0].GetString()
            .Should().Be("An order can have at most 50 lines. Remove some from your cart and review it again.");
        (await host.WithDbAsync(db => db.Orders.CountAsync(o => o.CustomerId == shopper.Id, Ct))).Should().Be(0);
    }

    [Fact]
    [Trait("BR", "BR-ADM-01")]
    public async Task BR_ADM_01_the_largest_order_at_the_highest_own_price_is_placed_instead_of_overflowing_the_order_columns()
    {
        // 50 lines of 10 units at the highest price the admin may set: before the cap, one such line could already
        // overflow the numeric(12,2) order columns, and Place order answered 500.
        var shopper = await host.NewShopperAsync();
        var products = Enumerable.Range(0, PlaceOrderRequest.MaxLines)
            .Select(i => CatalogTestSupport.Own($"Largest order item {i} {Guid.NewGuid():N}", PriceLimits.MaxUnitPriceUsd, onHand: 10))
            .ToArray();
        await host.InsertAsync(products);
        foreach (var product in products)
        {
            await shopper.AddToCartAsync(product, 10);
        }

        var preview = await shopper.PreviewAsync();
        var response = await shopper.PlaceAsync(preview);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var order = await response.ReadAsync<OrderDetailDto>();
        // Per line: 10 x 999,999.99 = 9,999,999.90, tax 8 % = 799,999.99 (rounded per line); Standard shipping 4.99.
        (order.Totals.Subtotal, order.Totals.Tax, order.Totals.Shipping, order.Totals.Total)
            .Should().Be((499_999_995.00m, 39_999_999.50m, 4.99m, 539_999_999.49m));
        (await host.OrderAsync(order.Number)).Total.Should().Be(539_999_999.49m);
    }
}
