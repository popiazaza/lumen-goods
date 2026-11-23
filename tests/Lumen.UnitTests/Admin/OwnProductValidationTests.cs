using Lumen.Api.Admin;
using Lumen.Api.Carts;
using Lumen.Api.Catalog;
using Lumen.Api.Checkout;
using Lumen.Domain.Catalog;
using Lumen.Domain.Pricing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace Lumen.UnitTests.Admin;

/// <summary>
/// The own-product price cap (BR-ADM-01): the admin form's limit, and the arithmetic behind it, which keeps the largest
/// possible order inside the order tables' numeric(12,2) money columns.
/// </summary>
public sealed class OwnProductValidationTests
{
    /// <summary>The largest value a numeric(12,2) column holds (Orders.Total and the other order money columns).</summary>
    private const decimal OrderColumnMax = 9_999_999_999.99m;

    private static CreateOwnProductRequest Product(decimal price) =>
        new(Sku: null, Name: "Enamel Camp Mug", Description: "", Category: ProductCategories.Home, Price: price,
            Attributes: null, Images: null, OnHand: 10);

    private static Dictionary<string, string[]> Validate(decimal price) =>
        OwnProductValidation.Validate(Product(price), new OwnMediaLibrary(new NoMedia()), out _);

    [Fact]
    [Trait("BR", "BR-ADM-01")]
    public void BR_ADM_01_a_price_up_to_999_999_99_is_accepted_and_a_cent_more_is_refused()
    {
        OwnProductValidation.MaxPrice.Should().Be(999_999.99m);

        Validate(999_999.99m).Should().BeEmpty();
        Validate(1_000_000.00m).Should().BeEquivalentTo(new Dictionary<string, string[]>
        {
            ["price"] = ["Price can be at most $999,999.99."],
        });
        Validate(9_999_999_999.99m).Should().ContainKey("price", "the old maximum overflowed the order columns");
    }

    [Fact]
    [Trait("BR", "BR-ADM-01")]
    public void BR_ADM_01_the_largest_order_at_the_highest_own_price_fits_the_order_columns_with_every_setting_at_its_maximum()
    {
        // The worst case the cap must survive: as many lines and units as an order takes, tax at 100 % and the highest
        // fees, with a line from each seller so all three shipments are charged.
        var pricing = new PricingService(new PricingOptions { TaxRate = 1m, StandardShippingFee = 1000m, ExpressShippingFee = 1000m });

        var largest = pricing.Price(LargestOrder(OwnProductValidation.MaxPrice), ShippingMethod.Express);
        var oneDigitMore = pricing.Price(LargestOrder(OwnProductValidation.MaxPrice * 10 + 0.09m), ShippingMethod.Express);

        (largest.Subtotal, largest.TaxTotal, largest.ShippingTotal, largest.GrandTotal)
            .Should().Be((499_999_995.00m, 499_999_995.00m, 3_000.00m, 1_000_002_990.00m));
        largest.GrandTotal.Should().BeLessThanOrEqualTo(OrderColumnMax);
        oneDigitMore.GrandTotal.Should().Be(10_000_002_990.00m).And.BeGreaterThan(OrderColumnMax);
    }

    /// <summary>
    /// <see cref="PlaceOrderRequest.MaxLines"/> lines of <see cref="CartLine.MaxQuantity"/> units each: own products,
    /// plus one Harbor and one Kestrel line at the same price.
    /// </summary>
    private static List<PricingLine> LargestOrder(decimal unitPrice)
    {
        PlaceOrderRequest.MaxLines.Should().Be(CartOptions.MaxLinesCeiling).And.Be(50);
        var sources = Enumerable.Repeat(ProductSources.Lumen, PlaceOrderRequest.MaxLines - 2).Append(ProductSources.Harbor).Append(ProductSources.Kestrel);
        return [.. sources.Select(source => new PricingLine(Guid.NewGuid(), source, unitPrice, CartLine.MaxQuantity))];
    }

    /// <summary>The products here have no images, so the media library is never asked; this keeps it empty anyway.</summary>
    private sealed class NoMedia : IWebHostEnvironment
    {
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = "";

        public string ApplicationName { get; set; } = "Lumen.UnitTests";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = "";

        public string EnvironmentName { get; set; } = "Testing";
    }
}
