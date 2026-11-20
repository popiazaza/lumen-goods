using Lumen.Api.Carts;
using Lumen.Domain.Catalog;
using Lumen.IntegrationTests.Infrastructure;
using Lumen.IntegrationTests.Orders;
using Microsoft.EntityFrameworkCore;

namespace Lumen.IntegrationTests.Carts;

/// <summary>The cart tables on real PostgreSQL: who owns a cart, and the per-line limits.</summary>
public sealed class CartPersistenceTests(LumenApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Cart GuestCart() => new()
    {
        AnonymousTokenHash = GuestCartToken.Hash(GuestCartToken.NewToken()),
        CreatedUtc = OrderingTestData.Now,
        UpdatedUtc = OrderingTestData.Now,
    };

    private static CartLine Line(Guid productId, int quantity = 1) => new()
    {
        ProductId = productId,
        Quantity = quantity,
        UnitPriceSeenUsd = 18.50m,
        AvailabilitySeen = AvailabilityStatus.InStock,
        SellableSeen = true,
        AddedUtc = OrderingTestData.Now,
    };

    [Fact]
    public async Task A_cart_with_its_lines_round_trips_and_keeps_only_the_hash_of_the_guest_token()
    {
        var product = await factory.NewProductAsync();
        var token = GuestCartToken.NewToken();
        var cart = GuestCart();
        cart.AnonymousTokenHash = GuestCartToken.Hash(token);
        cart.Lines.Add(Line(product.Id, quantity: 3));
        await factory.SaveAsync(cart);

        await using var db = factory.NewContext();
        var hash = GuestCartToken.Hash(token);
        var loaded = await db.Carts.Include(c => c.Lines).SingleAsync(c => c.AnonymousTokenHash == hash, Ct);

        loaded.Id.Should().Be(cart.Id);
        loaded.Lines.Should().ContainSingle().Which.Quantity.Should().Be(3);
        loaded.AnonymousTokenHash.Should().HaveCount(GuestCartToken.HashLength).And.NotEqual(System.Text.Encoding.UTF8.GetBytes(token));
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_a_cart_belongs_to_a_guest_or_a_customer_never_both_and_never_neither()
    {
        var customerId = await factory.NewCustomerAsync();
        var both = GuestCart();
        both.CustomerId = customerId;
        (await factory.SaveRejectedAsync(both)).ShouldBeCheckViolation("CK_Carts_GuestOrCustomer");

        var neither = GuestCart();
        neither.AnonymousTokenHash = null;
        (await factory.SaveRejectedAsync(neither)).ShouldBeCheckViolation("CK_Carts_GuestOrCustomer");
    }

    [Fact]
    public async Task The_guest_token_hash_is_a_sha256_and_unique()
    {
        var shortHash = GuestCart();
        shortHash.AnonymousTokenHash = [1, 2, 3];
        (await factory.SaveRejectedAsync(shortHash)).ShouldBeCheckViolation("CK_Carts_TokenHashIsSha256");

        var first = GuestCart();
        await factory.SaveAsync(first);
        var sameToken = GuestCart();
        sameToken.AnonymousTokenHash = first.AnonymousTokenHash;
        (await factory.SaveRejectedAsync(sameToken)).ShouldBeUniqueViolation("IX_Carts_AnonymousTokenHash");
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_a_customer_has_one_saved_cart()
    {
        var customerId = await factory.NewCustomerAsync();
        Cart Saved() => new() { CustomerId = customerId, CreatedUtc = OrderingTestData.Now, UpdatedUtc = OrderingTestData.Now };
        await factory.SaveAsync(Saved());

        (await factory.SaveRejectedAsync(Saved())).ShouldBeUniqueViolation("IX_Carts_CustomerId");
    }

    [Theory]
    [Trait("BR", "BR-CART-03")]
    [InlineData(0)]
    [InlineData(11)]
    public async Task BR_CART_03_a_line_holds_1_to_10_units(int quantity)
    {
        var product = await factory.NewProductAsync();
        var cart = GuestCart();
        cart.Lines.Add(Line(product.Id, quantity));

        (await factory.SaveRejectedAsync(cart)).ShouldBeCheckViolation("CK_CartLines_Quantity");
    }

    [Fact]
    [Trait("BR", "BR-CART-03")]
    public async Task BR_CART_03_ten_units_is_allowed_and_a_product_has_one_line_per_cart()
    {
        var product = await factory.NewProductAsync();
        var cart = GuestCart();
        cart.Lines.Add(Line(product.Id, CartLine.MaxQuantity));
        await factory.SaveAsync(cart);

        var duplicate = Line(product.Id);
        duplicate.CartId = cart.Id;
        (await factory.SaveRejectedAsync(duplicate)).ShouldBeUniqueViolation("IX_CartLines_CartId_ProductId");
    }

    [Fact]
    [Trait("BR", "BR-CART-01")]
    public async Task BR_CART_01_a_capped_quantity_remembers_a_larger_request_only()
    {
        var product = await factory.NewProductAsync();
        var cart = GuestCart();
        var line = Line(product.Id, quantity: 10);
        line.QuantityLimitedFrom = 10;
        cart.Lines.Add(line);
        (await factory.SaveRejectedAsync(cart)).ShouldBeCheckViolation("CK_CartLines_QuantityLimitedFrom");

        line.QuantityLimitedFrom = 14;
        await factory.SaveAsync(cart);
    }

    [Fact]
    public async Task A_seen_price_is_never_negative()
    {
        var product = await factory.NewProductAsync();
        var cart = GuestCart();
        var line = Line(product.Id);
        line.UnitPriceSeenUsd = -0.01m;
        cart.Lines.Add(line);

        (await factory.SaveRejectedAsync(cart)).ShouldBeCheckViolation("CK_CartLines_UnitPriceSeen");
    }
}
