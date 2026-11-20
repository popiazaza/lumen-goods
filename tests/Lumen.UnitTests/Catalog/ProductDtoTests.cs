using System.Text.Json;
using Lumen.Api.Catalog;
using Lumen.Api.Infrastructure.Json;
using Lumen.Domain.Catalog;

namespace Lumen.UnitTests.Catalog;

/// <summary>
/// The storefront product format from the spec, checked with the API's own JSON settings (<see cref="JsonDefaults"/>,
/// the same ones the HTTP pipeline uses), so this is what clients actually receive.
/// </summary>
public sealed class ProductDtoTests
{
    private static readonly JsonSerializerOptions WebJson = JsonDefaults.Create();
    private static readonly AvailabilityPolicy Policy = new();

    private static Product PartnerProduct() => new()
    {
        Id = Guid.Parse("01938f2a-0000-7000-8000-000000000001"),
        Source = ProductSources.Kestrel,
        SourceProductId = "K-7731",
        Name = "Merino Trail Socks",
        Description = "<p>Cushioned merino socks.</p>",
        Category = ProductCategories.Apparel,
        PriceUsd = 49.9m,
        SourcePrice = 45.99m,
        SourceCurrency = "EUR",
        FxRateToUsd = 1.085m,
        Quantity = 3,
        Images = [new ProductImage("http://kestrel:8080/img/k7731_l.png", true)],
        Attributes = new() { ["color"] = "Charcoal", ["size"] = "M" },
        FirstSeenUtc = DateTimeOffset.Parse("2026-11-01T05:00:00Z"),
        LastUpdatedUtc = DateTimeOffset.Parse("2026-11-01T05:00:00Z"),
    };

    private static JsonElement Serialize(Product product) =>
        JsonSerializer.SerializeToElement(product.ToDto(Policy, "http://localhost:8080"), WebJson);

    [Fact]
    public void Uses_the_spec_field_names()
    {
        var json = Serialize(PartnerProduct());

        json.EnumerateObject().Select(p => p.Name).Should().Equal(
            "id", "source", "sourceProductId", "name", "description", "category", "price", "images", "attributes",
            "availability", "sellable", "lastUpdatedUtc");
    }

    [Fact]
    public void Price_is_usd_with_exactly_two_decimals()
    {
        var price = Serialize(PartnerProduct()).GetProperty("price");

        price.GetProperty("amount").GetRawText().Should().Be("49.90");
        price.GetProperty("currency").GetString().Should().Be("USD");
    }

    [Fact]
    [Trait("BR", "BR-CAT-01")]
    public void Low_stock_quantity_is_reported_with_its_quantity()
    {
        var availability = Serialize(PartnerProduct()).GetProperty("availability");

        availability.GetProperty("status").GetString().Should().Be("low_stock");
        availability.GetProperty("quantity").GetInt32().Should().Be(3);
    }

    [Fact]
    public void Unknown_quantity_is_null_and_a_product_without_a_price_is_not_sellable()
    {
        var product = PartnerProduct();
        product.Quantity = null;
        product.StatusWhenQuantityUnknown = AvailabilityStatus.InStock;
        product.PriceUsd = null;

        var json = Serialize(product);

        json.GetProperty("availability").GetProperty("quantity").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("availability").GetProperty("status").GetString().Should().Be("in_stock");
        json.GetProperty("price").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("sellable").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Image_urls_are_absolute_lumen_urls_and_never_expose_the_partner_host()
    {
        var image = Serialize(PartnerProduct()).GetProperty("images")[0];

        var url = image.GetProperty("url").GetString();
        url.Should().MatchRegex(@"^http://localhost:8080/media/01938f2a-0000-7000-8000-000000000001/0\?v=[0-9a-f]{12}$");
        url.Should().NotContain("kestrel");
        image.GetProperty("isPrimary").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Image_urls_change_when_the_image_source_changes_so_a_day_of_browser_caching_is_safe()
    {
        var product = PartnerProduct();
        var before = product.ToDto(Policy, "http://localhost:8080").Images[0].Url;

        product.Images = [new ProductImage("http://kestrel:8080/img/k7731_l_v2.png", true)];
        var after = product.ToDto(Policy, "http://localhost:8080").Images[0].Url;
        product.PriceUsd = 12.34m;
        var afterPriceChange = product.ToDto(Policy, "http://localhost:8080").Images[0].Url;

        after.Should().NotBe(before);
        afterPriceChange.Should().Be(after, "only the image source versions the URL, not every product change");
    }

    [Fact]
    [Trait("BR", "BR-CAT-02")]
    public void BR_CAT_02_an_unlisted_product_still_resolves_but_is_not_sellable()
    {
        var product = PartnerProduct();
        product.IsListed = false;

        var json = Serialize(product);

        json.GetProperty("sellable").GetBoolean().Should().BeFalse();
        json.GetProperty("price").GetProperty("amount").GetDecimal().Should().Be(49.90m, "the last known price is still shown");
    }

    [Fact]
    public void Own_products_report_on_hand_minus_reserved()
    {
        var own = PartnerProduct();
        own.Source = ProductSources.Lumen;
        own.Quantity = null;
        own.OnHand = 10;
        own.Reserved = 7;
        own.SourceCurrency = "USD";
        own.SourcePrice = null;
        own.FxRateToUsd = null;

        Serialize(own).GetProperty("availability").GetProperty("quantity").GetInt32().Should().Be(3);
    }

    [Fact]
    public void Last_updated_is_utc_with_z()
    {
        var product = PartnerProduct();
        product.LastUpdatedUtc = DateTimeOffset.Parse("2026-11-01T06:00:00+01:00");

        Serialize(product).GetProperty("lastUpdatedUtc").GetString().Should().Be("2026-11-01T05:00:00Z");
    }
}
