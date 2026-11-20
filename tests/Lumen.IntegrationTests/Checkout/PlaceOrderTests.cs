using System.Net;
using Lumen.Api.Checkout;
using Lumen.Api.Orders;
using Lumen.Domain.Orders;
using Lumen.Domain.Pricing;
using Lumen.IntegrationTests.Catalog;
using Microsoft.EntityFrameworkCore;

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

    [Fact]
    [Trait("BR", "BR-PAY-01")]
    public async Task BR_PAY_01_a_double_clicked_place_order_creates_one_order_and_both_get_it()
    {
        var shopper = await host.NewShopperAsync();
        var mug = await host.OwnProductAsync(onHand: 10);
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
        problem.Changes.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { ProductId = lastUnit.Id, Kind = CheckoutChangeKind.QuantityLimited, Available = 0, Message = "Only 0 left" });
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
}
