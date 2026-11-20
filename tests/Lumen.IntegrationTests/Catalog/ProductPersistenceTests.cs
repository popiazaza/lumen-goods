using Lumen.Api.Catalog;
using Lumen.Api.Infrastructure.Data;
using Lumen.Domain.Catalog;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lumen.IntegrationTests.Catalog;

/// <summary>The products table on real PostgreSQL: JSON columns, the concurrency token and the stock constraints.</summary>
public sealed class ProductPersistenceTests(LumenApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LumenDbContext NewContext() => factory.Services.CreateScope().ServiceProvider.GetRequiredService<LumenDbContext>();

    private static Product OwnProduct(int onHand = 10, int reserved = 0) => new()
    {
        Source = ProductSources.Lumen,
        SourceProductId = $"LG-{Guid.NewGuid():N}",
        Name = "Enamel Camp Mug",
        Category = ProductCategories.Outdoor,
        PriceUsd = 18.50m,
        OnHand = onHand,
        Reserved = reserved,
        Images = [new ProductImage("own-media/mug.png", true)],
        Attributes = new() { ["color"] = "Cream" },
        FirstSeenUtc = DateTimeOffset.UtcNow,
        LastUpdatedUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Json_columns_round_trip_and_in_place_edits_are_saved()
    {
        var product = OwnProduct();
        await using (var db = NewContext())
        {
            db.Products.Add(product);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = NewContext())
        {
            var loaded = await db.Products.SingleAsync(p => p.Id == product.Id, Ct);
            loaded.Attributes["size"] = "350 ml";
            loaded.Images.Add(new ProductImage("own-media/mug-side.png", false));
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = NewContext())
        {
            var reloaded = await db.Products.AsNoTracking().SingleAsync(p => p.Id == product.Id, Ct);
            reloaded.Attributes.Should().BeEquivalentTo(new Dictionary<string, string> { ["color"] = "Cream", ["size"] = "350 ml" });
            reloaded.Images.Should().HaveCount(2);
        }
    }

    [Fact]
    [Trait("BR", "BR-ADM-01")]
    public async Task BR_ADM_01_a_stale_edit_is_rejected_by_the_xmin_concurrency_token()
    {
        var product = OwnProduct();
        await using (var db = NewContext())
        {
            db.Products.Add(product);
            await db.SaveChangesAsync(Ct);
        }

        await using var first = NewContext();
        await using var second = NewContext();
        var a = await first.Products.SingleAsync(p => p.Id == product.Id, Ct);
        var b = await second.Products.SingleAsync(p => p.Id == product.Id, Ct);

        a.OnHand = 12;
        await first.SaveChangesAsync(Ct);
        b.OnHand = 3;

        await FluentActions.Awaiting(() => second.SaveChangesAsync(Ct)).Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    [Trait("BR", "BR-STOCK-01")]
    public async Task BR_STOCK_01_the_database_refuses_to_reserve_more_than_is_on_hand()
    {
        await using var db = NewContext();
        db.Products.Add(OwnProduct(onHand: 2, reserved: 3));

        await FluentActions.Awaiting(() => db.SaveChangesAsync(Ct)).Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Only_own_products_carry_on_hand_stock()
    {
        await using (var db = NewContext())
        {
            var partner = OwnProduct();
            partner.Source = ProductSources.Harbor;
            db.Products.Add(partner);
            await FluentActions.Awaiting(() => db.SaveChangesAsync(Ct)).Should().ThrowAsync<DbUpdateException>();
        }

        await using (var db = NewContext())
        {
            var own = OwnProduct();
            own.OnHand = null;
            db.Products.Add(own);
            await FluentActions.Awaiting(() => db.SaveChangesAsync(Ct)).Should().ThrowAsync<DbUpdateException>();
        }
    }

    [Fact]
    [Trait("BR", "BR-CAT-01")]
    public async Task BR_CAT_01_the_in_stock_rule_runs_as_sql()
    {
        var marker = $"stock-check-{Guid.NewGuid():N}";
        var available = OwnProduct(onHand: 3, reserved: 1);
        var fullyReserved = OwnProduct(onHand: 2, reserved: 2);
        var partnerUnknownButAvailable = new Product
        {
            Source = ProductSources.Harbor, SourceProductId = $"HSC-{Guid.NewGuid():N}", Name = "Tote",
            Category = ProductCategories.Bags, StatusWhenQuantityUnknown = AvailabilityStatus.InStock,
        };
        var partnerStockUnknown = new Product
        {
            Source = ProductSources.Kestrel, SourceProductId = $"K-{Guid.NewGuid():N}", Name = "Lamp",
            Category = ProductCategories.Home, StatusWhenQuantityUnknown = AvailabilityStatus.OutOfStock,
        };
        foreach (var p in new[] { available, fullyReserved, partnerUnknownButAvailable, partnerStockUnknown })
        {
            p.Description = marker;
        }

        await using (var db = NewContext())
        {
            db.Products.AddRange(available, fullyReserved, partnerUnknownButAvailable, partnerStockUnknown);
            await db.SaveChangesAsync(Ct);
        }

        await using var query = NewContext();
        var inStock = await query.Products.Where(p => p.Description == marker).Where(ProductQueries.IsInStock)
            .Select(p => p.Id).ToListAsync(Ct);

        inStock.Should().BeEquivalentTo([available.Id, partnerUnknownButAvailable.Id]);
    }
}
