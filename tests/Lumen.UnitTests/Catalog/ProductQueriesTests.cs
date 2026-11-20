using Lumen.Api.Catalog;
using Lumen.Domain.Catalog;

namespace Lumen.UnitTests.Catalog;

/// <summary>
/// The listing filters run as SQL (<see cref="ProductQueries"/>) while the product format is built in C#. These tests
/// prove both give the same answer for every combination, so "in stock only" never disagrees with the badge shown.
/// </summary>
public sealed class ProductQueriesTests
{
    private static readonly AvailabilityPolicy Policy = new();
    private static readonly Func<Product, bool> IsInStockSql = ProductQueries.IsInStock.Compile();
    private static readonly Func<Product, bool> IsSellableSql = ProductQueries.IsSellable.Compile();

    public static TheoryData<int?, int, int?, AvailabilityStatus> StockCases()
    {
        var data = new TheoryData<int?, int, int?, AvailabilityStatus>();
        foreach (var onHand in new int?[] { null, 0, 1, 5, 9 })
        foreach (var reserved in new[] { 0, 1, 5 })
        foreach (var quantity in new int?[] { null, 0, 3, 8 })
        foreach (var unknown in Enum.GetValues<AvailabilityStatus>())
        {
            if (onHand is not null && (reserved > onHand || quantity is not null))
            {
                continue; // impossible rows: the database check constraints forbid them
            }

            data.Add(onHand, onHand is null ? 0 : reserved, quantity, unknown);
        }

        return data;
    }

    [Theory]
    [Trait("BR", "BR-CAT-01")]
    [MemberData(nameof(StockCases))]
    public void BR_CAT_01_in_stock_filter_matches_the_availability_shown(int? onHand, int reserved, int? quantity, AvailabilityStatus unknown)
    {
        var product = Make(onHand, reserved, quantity, unknown, isActive: true, priceUsd: 10m);

        var shownAsAvailable = Policy.Describe(product.AvailableQuantity, product.StatusWhenQuantityUnknown).Status
            != AvailabilityStatus.OutOfStock;

        IsInStockSql(product).Should().Be(shownAsAvailable);
    }

    [Theory]
    [Trait("BR", "BR-CAT-02")]
    [InlineData(true, true, 10.0, true)]
    [InlineData(true, true, null, false)]
    [InlineData(false, true, 10.0, false)]
    [InlineData(true, false, 10.0, false)]
    [InlineData(false, false, 10.0, false)]
    [InlineData(true, false, null, false)]
    public void BR_CAT_02_sellable_needs_an_active_listed_priced_product(bool isActive, bool isListed, double? price, bool expected)
    {
        var product = Make(null, 0, 3, AvailabilityStatus.InStock, isActive, (decimal?)price);
        product.IsListed = isListed;

        IsSellableSql(product).Should().Be(expected);
        product.IsSellable.Should().Be(expected);
    }

    private static Product Make(int? onHand, int reserved, int? quantity, AvailabilityStatus unknown, bool isActive, decimal? priceUsd) => new()
    {
        Source = onHand is null ? ProductSources.Harbor : ProductSources.Lumen,
        SourceProductId = "X-1",
        Name = "Test",
        Category = ProductCategories.Home,
        OnHand = onHand,
        Reserved = reserved,
        Quantity = quantity,
        StatusWhenQuantityUnknown = unknown,
        IsActive = isActive,
        PriceUsd = priceUsd,
    };
}
