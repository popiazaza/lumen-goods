using Lumen.Api.Catalog;

namespace Lumen.UnitTests.Catalog;

public sealed class ProductSearchCriteriaTests
{
    private static readonly string[] Sellers = ["lumen", "harbor", "kestrel"];

    private static ProductSearchCriteria Parse(ProductSearchParameters input)
    {
        ProductSearchCriteria.TryParse(input, Sellers, out var criteria, out var errors).Should().BeTrue(string.Join("; ", errors.Keys));
        return criteria!;
    }

    [Fact]
    public void An_empty_query_is_the_first_page_of_24_featured_with_no_filters()
    {
        var criteria = Parse(new ProductSearchParameters());

        criteria.Should().BeEquivalentTo(new ProductSearchCriteria(
            null, [], [], null, null, InStockOnly: false, ProductSort.Featured, new PageRequest(1, 24)),
            options => options.ComparingByMembers<ProductSearchCriteria>());
    }

    [Fact]
    public void Values_are_trimmed_lower_cased_and_de_duplicated()
    {
        var criteria = Parse(new ProductSearchParameters
        {
            Q = "  wool socks ",
            Category = ["Apparel", "apparel ", "", "BAGS"],
            Seller = ["Harbor", "harbor"],
            MinPrice = "10",
            MaxPrice = "49.90",
            InStock = "True",
            Sort = "PRICE_DESC",
            Page = "3",
            PageSize = "96",
        });

        criteria.Should().BeEquivalentTo(new ProductSearchCriteria(
            "wool socks", ["apparel", "bags"], ["harbor"], 10m, 49.90m, true, ProductSort.PriceDesc, new PageRequest(3, 96)),
            options => options.ComparingByMembers<ProductSearchCriteria>().WithStrictOrdering());
        criteria.Paging.Skip.Should().Be(192);
    }

    [Theory]
    [InlineData("1,000")]
    [InlineData("1e3")]
    [InlineData("-5")]
    [InlineData("12,50")]
    public void Prices_only_accept_plain_invariant_decimals(string price)
    {
        var ok = ProductSearchCriteria.TryParse(new ProductSearchParameters { MinPrice = price }, Sellers, out _, out var errors);

        ok.Should().BeFalse();
        errors.Should().ContainKey("minPrice");
    }

    [Fact]
    public void An_equal_min_and_max_price_is_allowed()
    {
        Parse(new ProductSearchParameters { MinPrice = "20", MaxPrice = "20.00" }).MinPrice.Should().Be(20m);
    }

    [Fact]
    public void A_page_whose_offset_does_not_fit_is_rejected()
    {
        var ok = ProductSearchCriteria.TryParse(
            new ProductSearchParameters { Page = int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), PageSize = "96" },
            Sellers, out _, out var errors);

        ok.Should().BeFalse();
        errors.Should().ContainKey("page");
    }

    [Theory]
    [InlineData("100%", @"%100\%%")]
    [InlineData("a_b", @"%a\_b%")]
    [InlineData(@"c\d", @"%c\\d%")]
    [InlineData("plain", "%plain%")]
    [Trait("BR", "BR-CAT-03")]
    public void BR_CAT_03_search_text_is_matched_literally(string term, string pattern)
    {
        LikePattern.Contains(term).Should().Be(pattern);
    }

    [Theory]
    [InlineData(0, 24, 0)]
    [InlineData(1, 24, 1)]
    [InlineData(24, 24, 1)]
    [InlineData(25, 24, 2)]
    [InlineData(5, 2, 3)]
    public void Total_pages_round_up(int totalCount, int pageSize, int totalPages)
    {
        PagedResult<int>.Create([], new PageRequest(1, pageSize), totalCount).TotalPages.Should().Be(totalPages);
    }

    [Fact]
    public void The_seller_list_must_include_lumen_and_have_unique_lower_case_sources()
    {
        static CatalogOptions With(params (string Source, string Name)[] sellers) =>
            new() { Sellers = [.. sellers.Select(s => new SellerOptions { Source = s.Source, Name = s.Name })] };

        CatalogOptions.IsValidSellerList(With(("lumen", "Lumen Goods"), ("harbor", "Harbor Supply Co."))).Should().BeTrue();
        CatalogOptions.IsValidSellerList(With()).Should().BeFalse();
        CatalogOptions.IsValidSellerList(With(("harbor", "Harbor Supply Co."))).Should().BeFalse();
        CatalogOptions.IsValidSellerList(With(("lumen", "Lumen Goods"), ("lumen", "Again"))).Should().BeFalse();
        CatalogOptions.IsValidSellerList(With(("lumen", "Lumen Goods"), ("Harbor", "Harbor Supply Co."))).Should().BeFalse();
        CatalogOptions.IsValidSellerList(With(("lumen", " "))).Should().BeFalse();
    }
}
