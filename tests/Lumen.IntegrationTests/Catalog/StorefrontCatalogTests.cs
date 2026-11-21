using System.Net;
using Lumen.Api.Catalog;
using Lumen.Api.Partners;
using Lumen.Domain.Catalog;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static Lumen.IntegrationTests.Catalog.CatalogTestSupport;

namespace Lumen.IntegrationTests.Catalog;

/// <summary>GET /api/v1/products, /products/{id} and /sellers on real PostgreSQL.</summary>
public sealed class StorefrontCatalogTests(LumenApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<PagedResult<ProductDto>> ListAsync(string query)
    {
        var response = await _client.GetAsync($"/api/v1/products?{query}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadAsync<PagedResult<ProductDto>>();
    }

    private async Task<List<string>> NamesAsync(string query) => [.. (await ListAsync(query)).Items.Select(p => p.Name)];

    [Fact]
    public async Task Paging_metadata_counts_every_match_and_a_page_past_the_end_is_empty()
    {
        var m = NewMarker();
        await factory.InsertAsync([.. Enumerable.Range(1, 5).Select(i => Own($"{m} Item {i}"))]);

        var first = await ListAsync($"q={m}&pageSize=2");
        var last = await ListAsync($"q={m}&pageSize=2&page=3");
        var beyond = await ListAsync($"q={m}&pageSize=2&page=4");

        first.Should().BeEquivalentTo(new { Page = 1, PageSize = 2, TotalCount = 5, TotalPages = 3 });
        first.Items.Should().HaveCount(2);
        last.Items.Should().ContainSingle();
        beyond.Items.Should().BeEmpty();
        beyond.TotalCount.Should().Be(5);
        beyond.TotalPages.Should().Be(3);
    }

    [Fact]
    public async Task Defaults_are_page_1_of_24()
    {
        var page = await ListAsync("");

        page.Page.Should().Be(1);
        page.PageSize.Should().Be(24);
    }

    [Fact]
    [Trait("BR", "BR-CAT-02")]
    public async Task BR_CAT_02_unlisted_products_are_hidden_from_the_list_but_still_resolve_by_id()
    {
        var m = NewMarker();
        var listed = Own($"{m} Listed");
        var deactivated = Own($"{m} Deactivated", listed: false);
        var dropped = Partner(ProductSources.Harbor, $"{m} Dropped by partner", listed: false);
        await factory.InsertAsync(listed, deactivated, dropped);

        var names = await NamesAsync($"q={m}");
        var byId = await _client.GetAsync($"/api/v1/products/{deactivated.Id}", Ct);

        names.Should().Equal($"{m} Listed");
        byId.StatusCode.Should().Be(HttpStatusCode.OK);
        var product = await byId.ReadAsync<ProductDto>();
        product.Name.Should().Be($"{m} Deactivated");
        product.Sellable.Should().BeFalse();
    }

    [Theory]
    [InlineData("0193a000-0000-7000-8000-00000000dead")]
    [InlineData("not-a-guid")]
    public async Task Unknown_product_id_is_a_404_problem(string id)
    {
        var response = await _client.GetAsync($"/api/v1/products/{id}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    [Trait("BR", "BR-CAT-03")]
    public async Task BR_CAT_03_search_is_case_insensitive_over_name_and_description()
    {
        var m = NewMarker();
        var d = NewMarker();
        await factory.InsertAsync(
            Own($"{m} Harbour LANTERN"),
            Own($"{m} Desk lamp", description: $"Brass shade with a {d} finish."),
            Own($"{m} Wool socks"));

        var byName = await NamesAsync($"q={Uri.EscapeDataString($"{m} harbour lantern".ToUpperInvariant())}");
        var byDescription = await NamesAsync($"q={d.ToUpperInvariant()}");

        byName.Should().Equal($"{m} Harbour LANTERN");
        byDescription.Should().Equal($"{m} Desk lamp");
    }

    [Fact]
    [Trait("BR", "BR-CAT-03")]
    public async Task BR_CAT_03_search_treats_like_wildcards_and_backslashes_literally()
    {
        var m = NewMarker();
        await factory.InsertAsync(
            Own($"{m} 100% wool"),
            Own($"{m} 100 wool"),
            Own($"{m} a_b"),
            Own($"{m} axb"),
            Own($@"{m} c\d"),
            Own($"{m} cd"));

        (await NamesAsync($"q={Uri.EscapeDataString($"{m} 100%")}")).Should().Equal($"{m} 100% wool");
        (await NamesAsync($"q={Uri.EscapeDataString($"{m} a_b")}")).Should().Equal($"{m} a_b");
        (await NamesAsync($"q={Uri.EscapeDataString($@"{m} c\d")}")).Should().Equal($@"{m} c\d");
    }

    [Fact]
    public async Task Category_filter_takes_several_values()
    {
        var m = NewMarker();
        await factory.InsertAsync(
            Own($"{m} Shirt", category: ProductCategories.Apparel),
            Own($"{m} Tote", category: ProductCategories.Bags),
            Own($"{m} Mug", category: ProductCategories.Home));

        var names = await NamesAsync($"q={m}&category=apparel&category=bags&sort=name_asc");

        names.Should().Equal($"{m} Shirt", $"{m} Tote");
    }

    [Fact]
    public async Task Seller_filter_takes_several_values()
    {
        var m = NewMarker();
        await factory.InsertAsync(
            Own($"{m} Lumen"),
            Partner(ProductSources.Harbor, $"{m} Harbor"),
            Partner(ProductSources.Kestrel, $"{m} Kestrel"));

        (await NamesAsync($"q={m}&seller=harbor&seller=kestrel&sort=name_asc")).Should().Equal($"{m} Harbor", $"{m} Kestrel");
        (await NamesAsync($"q={m}&seller=lumen")).Should().Equal($"{m} Lumen");
    }

    [Fact]
    [Trait("BR", "BR-CAT-03")]
    public async Task BR_CAT_03_price_range_is_inclusive_and_leaves_out_unpriced_products()
    {
        var m = NewMarker();
        await factory.InsertAsync(
            Own($"{m} A 9.99", price: 9.99m),
            Own($"{m} B 10.00", price: 10.00m),
            Own($"{m} C 25.50", price: 25.50m),
            Own($"{m} D 25.51", price: 25.51m),
            Partner(ProductSources.Harbor, $"{m} E price on request", price: null));

        (await NamesAsync($"q={m}&minPrice=10&maxPrice=25.50&sort=name_asc")).Should().Equal($"{m} B 10.00", $"{m} C 25.50");
        (await NamesAsync($"q={m}&minPrice=0&sort=name_asc")).Should().HaveCount(4).And.NotContain($"{m} E price on request");
        (await NamesAsync($"q={m}&maxPrice=10&sort=name_asc")).Should().Equal($"{m} A 9.99", $"{m} B 10.00");
    }

    [Fact]
    [Trait("BR", "BR-CAT-03")]
    public async Task BR_CAT_03_in_stock_only_keeps_products_that_can_be_bought_now()
    {
        var m = NewMarker();
        await factory.InsertAsync(
            Own($"{m} Own available", onHand: 3, reserved: 1),
            Own($"{m} Own fully reserved", onHand: 2, reserved: 2),
            Own($"{m} Own none on hand", onHand: 0),
            Partner(ProductSources.Harbor, $"{m} Partner low stock", quantity: 2),
            Partner(ProductSources.Harbor, $"{m} Partner sold out", quantity: 0),
            Partner(ProductSources.Harbor, $"{m} Partner available, quantity unknown", quantity: null, whenUnknown: AvailabilityStatus.InStock),
            Partner(ProductSources.Kestrel, $"{m} Partner stock not read yet", quantity: null, whenUnknown: AvailabilityStatus.OutOfStock),
            Partner(ProductSources.Harbor, $"{m} Partner in stock but unpriced", price: null, quantity: 50),
            Partner(ProductSources.Harbor, $"{m} Partner discontinued but still listed", quantity: 50, active: false));

        var names = await NamesAsync($"q={m}&inStock=true&sort=name_asc");
        var all = await NamesAsync($"q={m}&inStock=false");

        names.Should().Equal($"{m} Own available", $"{m} Partner available, quantity unknown", $"{m} Partner low stock");
        all.Should().HaveCount(9);
    }

    [Fact]
    [Trait("BR", "BR-CAT-01")]
    public async Task BR_CAT_01_own_product_availability_is_on_hand_minus_reserved()
    {
        var m = NewMarker();
        var low = Own($"{m} Low", onHand: 10, reserved: 7);
        var plenty = Own($"{m} Plenty", onHand: 20, reserved: 2);
        var gone = Own($"{m} Gone", onHand: 4, reserved: 4);
        await factory.InsertAsync(low, plenty, gone);

        var byName = (await ListAsync($"q={m}")).Items.ToDictionary(p => p.Name, p => p.Availability);

        byName[$"{m} Low"].Should().Be(new AvailabilityDto("low_stock", 3));
        byName[$"{m} Plenty"].Should().Be(new AvailabilityDto("in_stock", 18));
        byName[$"{m} Gone"].Should().Be(new AvailabilityDto("out_of_stock", 0));
    }

    [Fact]
    public async Task Filters_combine()
    {
        var m = NewMarker();
        await factory.InsertAsync(
            Own($"{m} Match", price: 30m, category: ProductCategories.Bags),
            Own($"{m} Wrong category", price: 30m, category: ProductCategories.Home),
            Own($"{m} Too expensive", price: 90m, category: ProductCategories.Bags),
            Own($"{m} Out of stock", price: 30m, onHand: 0, category: ProductCategories.Bags),
            Partner(ProductSources.Harbor, $"{m} Wrong seller", price: 30m, category: ProductCategories.Bags));

        var names = await NamesAsync($"q={m}&category=bags&seller=lumen&minPrice=20&maxPrice=50&inStock=true");

        names.Should().Equal($"{m} Match");
    }

    [Theory]
    [InlineData("price_asc", new[] { "Charlie", "Delta", "Alpha", "Bravo" })]
    [InlineData("price_desc", new[] { "Alpha", "Delta", "Charlie", "Bravo" })]
    [InlineData("name_asc", new[] { "Alpha", "Bravo", "Charlie", "Delta" })]
    [InlineData("newest", new[] { "Bravo", "Delta", "Alpha", "Charlie" })]
    [InlineData("", new[] { "Bravo", "Delta", "Alpha", "Charlie" })]
    [Trait("BR", "BR-CAT-03")]
    public async Task BR_CAT_03_sorts_put_unpriced_products_last_and_newest_means_first_seen(string sort, string[] expected)
    {
        var m = NewMarker();
        await factory.InsertAsync(
            Own($"{m} Alpha", price: 20.00m, firstSeen: BaseTime.AddDays(2)),
            Partner(ProductSources.Harbor, $"{m} Bravo", price: null, firstSeen: BaseTime.AddDays(4)),
            Own($"{m} Charlie", price: 5.00m, firstSeen: BaseTime.AddDays(1)),
            Own($"{m} Delta", price: 12.50m, firstSeen: BaseTime.AddDays(3)));

        var names = await NamesAsync($"q={m}&sort={sort}");

        names.Should().Equal(expected.Select(n => $"{m} {n}"));
    }

    [Theory]
    [InlineData("price_asc")]
    [InlineData("price_desc")]
    [InlineData("name_asc")]
    [InlineData("newest")]
    [Trait("BR", "BR-CAT-03")]
    public async Task BR_CAT_03_equal_sort_keys_are_ordered_by_id_so_no_item_repeats_or_vanishes_across_pages(string sort)
    {
        var m = NewMarker();
        var twins = Enumerable.Range(0, 7).Select(_ => Own($"{m} Twin", price: 15.00m)).ToList();
        await factory.InsertAsync([.. twins]);

        var paged = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            paged.AddRange((await ListAsync($"q={m}&sort={sort}&pageSize=3&page={page}")).Items.Select(p => p.Id));
        }

        // PostgreSQL orders uuids by their bytes, which is the ordinal order of the lower-case text form. The id
        // follows the direction of the main key: descending for newest and price_desc, ascending otherwise.
        var byId = twins.Select(t => t.Id.ToString()).Order(StringComparer.Ordinal).ToList();
        paged.Should().Equal(sort is "newest" or "price_desc" ? Enumerable.Reverse(byId) : byId);
    }

    [Theory]
    [InlineData("sort=cheapest", "sort")]
    [InlineData("category=toys", "category")]
    [InlineData("category=apparel&category=toys", "category")]
    [InlineData("seller=amazon", "seller")]
    [InlineData("page=0", "page")]
    [InlineData("page=two", "page")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=97", "pageSize")]
    [InlineData("minPrice=50&maxPrice=10", "minPrice")]
    [InlineData("minPrice=-1", "minPrice")]
    [InlineData("maxPrice=abc", "maxPrice")]
    [InlineData("inStock=maybe", "inStock")]
    [InlineData("q=%00", "q")]
    [InlineData("q=bad%00text", "q")]
    [InlineData("q=bell%07", "q")]
    public async Task Invalid_query_values_are_a_400_problem_naming_the_parameter(string query, string parameter)
    {
        var response = await _client.GetAsync($"/api/v1/products?{query}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.ReadJsonAsync()).GetProperty("errors").TryGetProperty(parameter, out _).Should().BeTrue();
    }

    [Fact]
    public async Task Sellers_come_from_configuration()
    {
        var sellers = await (await _client.GetAsync("/api/v1/sellers", Ct)).ReadAsync<List<SellerDto>>();

        sellers.Should().Equal(
            new SellerDto("lumen", "Lumen Goods"),
            new SellerDto("harbor", "Harbor Supply Co."),
            new SellerDto("kestrel", "Kestrel Outfitters"));
    }

    [Fact]
    public async Task Partner_image_urls_never_reach_the_client()
    {
        var m = NewMarker();
        var partner = Partner(ProductSources.Kestrel, $"{m} Lamp");
        await factory.InsertAsync(partner);

        var list = await _client.GetStringAsync($"/api/v1/products?q={m}", Ct);
        var detail = await (await _client.GetAsync($"/api/v1/products/{partner.Id}", Ct)).ReadAsync<ProductDto>();

        list.Should().NotContain("kestrel:8080").And.NotContain("partner-only");
        detail.Images.Should().ContainSingle().Which.Url.Should().Be(ProductMapping.ImageUrl("http://localhost", partner.Id, 0, partner.Images[0].SourceUrl));
    }

    [Fact]
    public async Task A_partner_product_says_when_lumen_last_checked_it_not_when_the_partner_last_edited_it()
    {
        var m = NewMarker();
        // A partner code of its own, so the sync state this test writes is not shared with the partner sync tests.
        var source = $"t{Guid.NewGuid():N}"[..16];
        var catalogueRead = BaseTime.AddDays(19);
        var partner = Partner(source, $"{m} Lantern", firstSeen: BaseTime);
        var own = Own($"{m} Mug");
        await factory.InsertAsync(partner, own);
        await factory.WithDbAsync(async db =>
        {
            db.PartnerSyncStates.Add(new PartnerSyncState { Partner = source, LastSuccessUtc = catalogueRead });
            await db.SaveChangesAsync(Ct);
        });

        var detail = await (await _client.GetAsync($"/api/v1/products/{partner.Id}", Ct)).ReadAsync<ProductDto>();
        var listed = (await ListAsync($"q={m}")).Items.ToDictionary(p => p.Name);

        (detail.LastUpdatedUtc, detail.LastCheckedUtc).Should().Be((BaseTime.UtcDateTime, catalogueRead.UtcDateTime));
        listed[$"{m} Lantern"].LastCheckedUtc.Should().Be(catalogueRead.UtcDateTime);
        listed[$"{m} Mug"].LastCheckedUtc.Should().BeNull("Lumen's own price and stock are live");
    }

    [Fact]
    public async Task The_seed_creates_every_own_product_and_lists_all_but_the_inactive_one()
    {
        // Other tests add and deactivate own products in parallel, so this reads the seeded rows by SKU and fetches
        // each one by id rather than paging a list whose contents shift underneath it.
        List<Product> rows = [];
        await factory.WithDbAsync(async db => rows = await db.Products.AsNoTracking()
            .Where(p => p.Source == ProductSources.Lumen && OwnProductSeed.Skus.Contains(p.SourceProductId))
            .ToListAsync(Ct));
        var shown = new List<ProductDto>();
        foreach (var row in rows)
        {
            shown.Add(await (await _client.GetAsync($"/api/v1/products/{row.Id}", Ct)).ReadAsync<ProductDto>());
        }

        rows.Should().HaveCount(OwnProductSeed.Skus.Count);
        rows.Where(p => !p.IsListed).Select(p => p.SourceProductId).Should().Equal("LG-FW-004");
        shown.Select(p => p.Category).Distinct().Should().BeEquivalentTo(ProductCategories.All);
        shown.Should().ContainSingle(p => p.Images.Count == 0);
        shown.Should().Contain(p => p.Availability.Status == "out_of_stock");
        shown.Should().Contain(p => p.Availability.Status == "low_stock");
        shown.Single(p => p.SourceProductId == "LG-FW-004").Sellable.Should().BeFalse();
        (await NamesAsync("q=Waxed%20Canvas%20Weekender")).Should().Equal("Waxed Canvas Weekender");
        (await NamesAsync("q=Trail%20Running%20Shoes")).Should().BeEmpty();
    }
}
