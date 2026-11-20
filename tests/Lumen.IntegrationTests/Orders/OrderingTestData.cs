using Lumen.Api.Catalog;
using Lumen.Api.Identity;
using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Orders;
using Lumen.Api.Payments;
using Lumen.Domain.Catalog;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Lumen.Domain.Pricing;
using Lumen.IntegrationTests.Catalog;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Lumen.IntegrationTests.Orders;

/// <summary>
/// Builders for ordering rows that satisfy every database constraint, so a test changes exactly the one value it is
/// about. Each test gets its own customer and product, so tests never share a cart, wallet or order.
/// </summary>
internal static class OrderingTestData
{
    public static readonly DateTimeOffset Now = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    private static readonly OrderNumberGenerator Numbers = new();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A fresh customer account (no password: these tests never sign in as it).</summary>
    public static async Task<Guid> NewCustomerAsync(this LumenApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var email = $"c{Guid.NewGuid():N}@example.test";
        var user = new AppUser { UserName = email, Email = email };
        (await users.CreateAsync(user)).Succeeded.Should().BeTrue();
        return user.Id;
    }

    /// <summary>An own product, unlisted so it never shows up in the catalogue tests' listings.</summary>
    public static async Task<Product> NewProductAsync(this LumenApiFactory factory)
    {
        var product = CatalogTestSupport.Own($"Ordering test {Guid.NewGuid():N}", price: 18.50m, listed: false);
        await factory.InsertAsync(product);
        return product;
    }

    /// <summary>
    /// A placed order: one Lumen line of 2 × $18.50 (tax $2.96) and its Standard shipment ($4.99), total $44.95.
    /// </summary>
    public static Order NewOrder(Guid customerId, Guid productId) => new()
    {
        Number = Numbers.Next(),
        CustomerId = customerId,
        CheckoutKey = Guid.NewGuid(),
        Subtotal = 37.00m,
        ShippingTotal = 4.99m,
        TaxTotal = 2.96m,
        Total = 44.95m,
        ShipTo = new ShippingAddress("Ada Lovelace", "1200 NW Naito Pkwy", null, "Portland", "OR", "97209", "US"),
        ShippingMethodForOwnItems = ShippingMethod.Standard,
        CreatedUtc = Now,
        HoldExpiresUtc = Now.AddMinutes(15),
        Lines =
        [
            new OrderLine
            {
                LineNumber = 1,
                ProductId = productId,
                Source = ProductSources.Lumen,
                SourceProductId = "LG-AC-001",
                Name = "Enamel Camp Mug",
                PrimaryImageSourceUrl = "own-media/lg-ac-001.png",
                UnitPriceUsd = 18.50m,
                Quantity = 2,
                LineSubtotal = 37.00m,
                Tax = 2.96m,
                SourceUnitPrice = 18.50m,
            },
        ],
        Shipments = [new OrderShipment { Source = ProductSources.Lumen, Method = ShippingMethod.Standard, Fee = 4.99m }],
    };

    public static Payment NewPayment(Guid orderId, PaymentStatus status, PaymentMethod method = PaymentMethod.Card) => new()
    {
        OrderId = orderId,
        Method = method,
        Status = status,
        StatusChangedUtc = Now,
        Amount = 44.95m,
        CardBrand = method == PaymentMethod.Card ? CardBrand.Visa : null,
        CardLast4 = method == PaymentMethod.Card ? "1111" : null,
        CreatedUtc = Now,
    };

    /// <summary>A customer with a saved order, ready for payments, refunds and partner orders to be added.</summary>
    public static async Task<Order> NewSavedOrderAsync(this LumenApiFactory factory)
    {
        var customerId = await factory.NewCustomerAsync();
        var product = await factory.NewProductAsync();
        var order = NewOrder(customerId, product.Id);
        await factory.SaveAsync(order);
        return order;
    }

    public static LumenDbContext NewContext(this LumenApiFactory factory) =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<LumenDbContext>();

    public static async Task SaveAsync(this LumenApiFactory factory, params object[] entities)
    {
        await using var db = factory.NewContext();
        db.AddRange(entities);
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Saves the entities and returns the PostgreSQL error the database refused them with.</summary>
    public static async Task<PostgresException> SaveRejectedAsync(this LumenApiFactory factory, params object[] entities)
    {
        await using var db = factory.NewContext();
        db.AddRange(entities);
        var thrown = await FluentActions.Awaiting(() => db.SaveChangesAsync(Ct)).Should().ThrowAsync<DbUpdateException>();
        return thrown.Which.InnerException.Should().BeOfType<PostgresException>().Subject;
    }
}

/// <summary>The PostgreSQL errors the ordering constraints raise.</summary>
internal static class PostgresErrorExtensions
{
    /// <summary>A check constraint refused the row; <paramref name="constraint"/> names which.</summary>
    public static void ShouldBeCheckViolation(this PostgresException error, string constraint)
    {
        error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.Should().Be(constraint);
    }

    /// <summary>A unique index refused the row; <paramref name="index"/> names which.</summary>
    public static void ShouldBeUniqueViolation(this PostgresException error, string index)
    {
        error.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        error.ConstraintName.Should().Be(index);
    }
}
