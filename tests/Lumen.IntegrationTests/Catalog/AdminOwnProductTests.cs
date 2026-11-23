using System.Net;
using Lumen.Api.Admin;
using Lumen.Api.Catalog;
using Lumen.Api.Identity;
using Lumen.Domain.Catalog;
using Lumen.Domain.Pricing;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static Lumen.IntegrationTests.Catalog.CatalogTestSupport;

namespace Lumen.IntegrationTests.Catalog;

/// <summary>Admin page 9 ("Own products"): /api/v1/admin/products and the own-media library.</summary>
public sealed class AdminOwnProductTests(LumenApiFactory factory)
{
    private const string Products = "/api/v1/admin/products";

    private static CreateOwnProductRequest NewProduct(string name, decimal price = 49.90m, int onHand = 12) => new(
        Sku: null,
        Name: name,
        Description: "Plain-text description.",
        Category: ProductCategories.Home,
        Price: price,
        Attributes: new() { ["color"] = "Sand" },
        Images: ["lg-hm-001.png"],
        OnHand: onHand);

    private static UpdateOwnProductRequest EditOf(AdminProductDto p, string? version = null, int? onHand = null, string? name = null) => new(
        Version: version ?? p.Version,
        Name: name ?? p.Name,
        Description: p.Description,
        Category: p.Category,
        Price: p.Price,
        Attributes: new(p.Attributes),
        Images: [.. p.Images.Select(i => i.File)],
        OnHand: onHand ?? p.OnHand);

    private async Task<(ApiSession Admin, AdminProductDto Product)> CreateAsync(CreateOwnProductRequest request)
    {
        var admin = await factory.SignedInAsync(Roles.Admin);
        var response = await admin.PostAsJsonAsync(Products, request);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (admin, await response.ReadAsync<AdminProductDto>());
    }

    [Theory]
    [InlineData("/api/v1/admin/products")]
    [InlineData("/api/v1/admin/products/0193a000-0000-7000-8000-000000000001")]
    [InlineData("/api/v1/admin/own-media")]
    [Trait("BR", "BR-ADM-01")]
    public async Task BR_ADM_01_admin_endpoints_answer_401_to_anonymous_and_403_to_customers(string url)
    {
        var anonymous = await factory.CreateClient().GetAsync(url, Ct);
        var customer = await factory.SignedInAsync(Roles.Customer);
        var asCustomer = await customer.GetAsync(url);
        var customerCreate = await customer.PostAsJsonAsync(Products, NewProduct("Not allowed"));

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        asCustomer.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        customerCreate.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_created_product_is_sellable_in_the_storefront_straight_away()
    {
        var m = NewMarker();

        var (admin, created) = await CreateAsync(NewProduct($"{m} Stoneware Bowl"));
        var storefront = await (await factory.CreateClient().GetAsync($"/api/v1/products/{created.Id}", Ct)).ReadAsync<ProductDto>();
        var listed = await (await factory.CreateClient().GetAsync($"/api/v1/products?q={m}", Ct)).ReadAsync<PagedResult<ProductDto>>();

        created.Sku.Should().MatchRegex("^LG-[0-9A-F]{8}$");
        created.Should().BeEquivalentTo(new
        {
            Name = $"{m} Stoneware Bowl", Price = 49.90m, OnHand = 12, Reserved = 0, IsActive = true, Sellable = true,
            Availability = new AvailabilityDto("in_stock", 12),
        });
        created.Images.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new AdminImageDto("lg-hm-001.png", ProductMapping.ImageUrl("http://localhost", Guid.Parse(created.Id), 0, "own-media/lg-hm-001.png"), true));
        created.Version.Should().NotBeNullOrEmpty();
        storefront.Should().BeEquivalentTo(new
        {
            created.Id, Source = "lumen", SourceProductId = created.Sku, Name = $"{m} Stoneware Bowl", Category = "home",
            Price = new PriceDto(49.90m, "USD"), Sellable = true,
        });
        listed.Items.Should().ContainSingle(p => p.Id == created.Id);
        (await admin.GetAsync($"{Products}/{created.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_price_is_written_with_two_decimals()
    {
        var (_, created) = await CreateAsync(NewProduct($"{NewMarker()} Plate", price: 30m));
        var admin = await factory.SignedInAsync(Roles.Admin);

        var json = await (await admin.GetAsync($"{Products}/{created.Id}")).ReadJsonAsync();

        json.GetProperty("price").GetRawText().Should().Be("30.00");
    }

    [Fact]
    public async Task Attribute_keys_are_normalised_to_lower_camel_case()
    {
        var request = NewProduct($"{NewMarker()} Down Vest") with
        {
            Attributes = new() { ["Fill Power"] = "650", ["Colour"] = " Navy " },
        };

        var (_, created) = await CreateAsync(request);

        created.Attributes.Should().BeEquivalentTo(new Dictionary<string, string> { ["fillPower"] = "650", ["color"] = "Navy" });
    }

    [Fact]
    public async Task A_sku_already_in_use_is_a_409()
    {
        var sku = $"T-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        await CreateAsync(NewProduct($"{NewMarker()} First") with { Sku = sku });
        var admin = await factory.SignedInAsync(Roles.Admin);

        var second = await admin.PostAsJsonAsync(Products, NewProduct($"{NewMarker()} Second") with { Sku = sku.ToLowerInvariant() });

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    public static TheoryData<string, CreateOwnProductRequest> InvalidProducts() => new()
    {
        { "name", NewProduct("") },
        { "category", NewProduct("Bad category") with { Category = "toys" } },
        { "price", NewProduct("Free") with { Price = 0m } },
        { "price", NewProduct("Fractions of a cent") with { Price = 1.234m } },
        { "price", NewProduct("No price") with { Price = null } },
        { "onHand", NewProduct("Negative stock") with { OnHand = -1 } },
        { "images", NewProduct("Unknown image") with { Images = ["not-in-the-library.png"] } },
        { "images", NewProduct("Path traversal") with { Images = ["../appsettings.json"] } },
        { "attributes", NewProduct("Empty attribute") with { Attributes = new() { ["color"] = " " } } },
        { "sku", NewProduct("Bad SKU") with { Sku = "no spaces allowed" } },
        { "name", NewProduct("Nul\0name") },
        { "name", NewProduct("Two\nlines") },
        { "description", NewProduct("Nul in description") with { Description = "Line one\nline two\0" } },
        { "attributes", NewProduct("Nul in attribute") with { Attributes = new() { ["color"] = "Na\0vy" } } },
        { "attributes", NewProduct("Nul in attribute name") with { Attributes = new() { ["col\0or"] = "Navy" } } },
    };

    [Fact]
    [Trait("BR", "BR-ADM-01")]
    public async Task BR_ADM_01_the_price_can_be_at_most_999_999_99_so_the_largest_order_fits_the_order_columns()
    {
        var (admin, atMax) = await CreateAsync(NewProduct($"{NewMarker()} Brass Telescope", price: PriceLimits.MaxUnitPriceUsd));

        var above = await admin.PostAsJsonAsync(Products, NewProduct($"{NewMarker()} Gold Telescope", price: 1_000_000.00m));
        var editAbove = await admin.PutAsJsonAsync($"{Products}/{atMax.Id}", EditOf(atMax) with { Price = 1_000_000.00m });

        atMax.Price.Should().Be(999_999.99m);
        foreach (var refused in new[] { above, editAbove })
        {
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await refused.ReadJsonAsync()).GetProperty("errors").GetProperty("price")[0].GetString()
                .Should().Be("Price can be at most $999,999.99.");
        }
    }

    [Fact]
    public async Task A_description_keeps_its_line_breaks_and_tabs()
    {
        var description = "First line.\r\nSecond line.\n\tIndented line.";

        var (_, created) = await CreateAsync(NewProduct($"{NewMarker()} Notebook") with { Description = description });

        created.Description.Should().Be(description);
    }

    [Fact]
    public async Task A_list_search_with_a_control_character_is_a_400_naming_q()
    {
        var admin = await factory.SignedInAsync(Roles.Admin);

        var response = await admin.GetAsync($"{Products}?q=bad%00text");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("errors").TryGetProperty("q", out _).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(InvalidProducts))]
    public async Task Invalid_products_are_a_400_problem_naming_the_field(string field, CreateOwnProductRequest request)
    {
        var admin = await factory.SignedInAsync(Roles.Admin);

        var response = await admin.PostAsJsonAsync(Products, request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
    }

    [Fact]
    public async Task An_update_changes_the_fields_and_returns_a_new_version()
    {
        var m = NewMarker();
        var (admin, created) = await CreateAsync(NewProduct($"{m} Vase"));

        var response = await admin.PutAsJsonAsync($"{Products}/{created.Id}", EditOf(created, name: $"{m} Tall Vase", onHand: 3) with
        {
            Price = 64.00m,
            Category = ProductCategories.Accessories,
            Images = ["lg-hm-004.png", "lg-hm-001.png"],
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var updated = await response.ReadAsync<AdminProductDto>();
        updated.Should().BeEquivalentTo(new
        {
            Name = $"{m} Tall Vase", Price = 64.00m, Category = "accessories", OnHand = 3,
            Availability = new AvailabilityDto("low_stock", 3),
        });
        updated.Images.Select(i => (i.File, i.IsPrimary)).Should().Equal(("lg-hm-004.png", true), ("lg-hm-001.png", false));
        updated.Version.Should().NotBe(created.Version);
        updated.LastUpdatedUtc.Should().BeOnOrAfter(created.LastUpdatedUtc);
    }

    [Fact]
    [Trait("BR", "BR-ADM-01")]
    public async Task BR_ADM_01_an_edit_based_on_a_stale_version_is_rejected_with_409()
    {
        var (admin, created) = await CreateAsync(NewProduct($"{NewMarker()} Jug"));
        (await admin.PutAsJsonAsync($"{Products}/{created.Id}", EditOf(created, onHand: 20))).EnsureSuccessStatusCode();

        var stale = await admin.PutAsJsonAsync($"{Products}/{created.Id}", EditOf(created, onHand: 5));

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await stale.ReadJsonAsync()).GetProperty("title").GetString().Should().Be("Changed by someone else");
        var current = await (await admin.GetAsync($"{Products}/{created.Id}")).ReadAsync<AdminProductDto>();
        current.OnHand.Should().Be(20);
    }

    [Fact]
    [Trait("BR", "BR-ADM-01")]
    public async Task BR_ADM_01_parallel_edits_from_the_same_version_save_exactly_once()
    {
        // All requests can load the product before any of them saves, so they pass the in-memory version check and
        // race to the UPDATE; only the xmin condition in the database can then pick a single winner.
        var (admin, created) = await CreateAsync(NewProduct($"{NewMarker()} Kettle", onHand: 10));

        var responses = await Task.WhenAll(Enumerable.Range(1, 8).Select(i =>
            admin.PutAsJsonAsync($"{Products}/{created.Id}", EditOf(created, onHand: 100 + i))));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(7);
        var winner = await responses.Single(r => r.StatusCode == HttpStatusCode.OK).ReadAsync<AdminProductDto>();
        var stored = await (await admin.GetAsync($"{Products}/{created.Id}")).ReadAsync<AdminProductDto>();
        stored.OnHand.Should().Be(winner.OnHand);
        stored.Version.Should().Be(winner.Version);
    }

    [Fact]
    [Trait("BR", "BR-ADM-01")]
    public async Task BR_ADM_01_a_stock_reservation_since_the_product_was_loaded_makes_the_edit_stale()
    {
        var (admin, loaded) = await CreateAsync(NewProduct($"{NewMarker()} Teapot", onHand: 5));
        var id = Guid.Parse(loaded.Id);
        await factory.WithDbAsync(db => db.Products.Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Reserved, p => p.Reserved + 2), Ct));

        var response = await admin.PutAsJsonAsync($"{Products}/{loaded.Id}", EditOf(loaded, name: "Renamed while reserved"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    [Trait("BR", "BR-ADM-01")]
    public async Task BR_ADM_01_an_update_without_a_version_is_a_400()
    {
        var (admin, created) = await CreateAsync(NewProduct($"{NewMarker()} Cup"));

        var response = await admin.PutAsJsonAsync($"{Products}/{created.Id}", EditOf(created) with { Version = null });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("errors").TryGetProperty("version", out _).Should().BeTrue();
    }

    [Fact]
    [Trait("BR", "BR-ADM-01")]
    public async Task BR_ADM_01_on_hand_cannot_be_set_below_the_reserved_units()
    {
        var reserved = Own($"{NewMarker()} Reserved lamp", onHand: 5, reserved: 3);
        await factory.InsertAsync(reserved);
        var admin = await factory.SignedInAsync(Roles.Admin);
        var loaded = await (await admin.GetAsync($"{Products}/{reserved.Id}")).ReadAsync<AdminProductDto>();

        var below = await admin.PutAsJsonAsync($"{Products}/{reserved.Id}", EditOf(loaded, onHand: 2));
        var atReserved = await admin.PutAsJsonAsync($"{Products}/{reserved.Id}", EditOf(loaded, onHand: 3));

        below.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await below.ReadJsonAsync()).GetProperty("reserved").GetInt32().Should().Be(3);
        atReserved.StatusCode.Should().Be(HttpStatusCode.OK);
        (await atReserved.ReadAsync<AdminProductDto>()).Availability.Should().Be(new AvailabilityDto("out_of_stock", 0));
    }

    [Fact]
    [Trait("BR", "BR-ADM-01")]
    public async Task BR_ADM_01_deactivating_hides_the_product_but_keeps_it_and_reactivating_lists_it_again()
    {
        var m = NewMarker();
        var (admin, created) = await CreateAsync(NewProduct($"{m} Lantern"));
        var storefront = factory.CreateClient();

        var deactivated = await (await admin.PostAsJsonAsync($"{Products}/{created.Id}/deactivate", new { })).ReadAsync<AdminProductDto>();
        var hiddenList = await (await storefront.GetAsync($"/api/v1/products?q={m}", Ct)).ReadAsync<PagedResult<ProductDto>>();
        var stillResolves = await (await storefront.GetAsync($"/api/v1/products/{created.Id}", Ct)).ReadAsync<ProductDto>();
        var again = await (await admin.PostAsJsonAsync($"{Products}/{created.Id}/deactivate", new { })).ReadAsync<AdminProductDto>();
        var reactivated = await (await admin.PostAsJsonAsync($"{Products}/{created.Id}/reactivate", new { })).ReadAsync<AdminProductDto>();
        var shownList = await (await storefront.GetAsync($"/api/v1/products?q={m}", Ct)).ReadAsync<PagedResult<ProductDto>>();

        deactivated.Should().BeEquivalentTo(new { IsActive = false, Sellable = false });
        hiddenList.Items.Should().BeEmpty();
        stillResolves.Sellable.Should().BeFalse();
        again.IsActive.Should().BeFalse();
        reactivated.Should().BeEquivalentTo(new { IsActive = true, Sellable = true });
        shownList.Items.Should().ContainSingle(p => p.Id == created.Id);
    }

    [Fact]
    public async Task The_list_filters_by_status_and_search_and_shows_only_own_products()
    {
        var m = NewMarker();
        var (admin, active) = await CreateAsync(NewProduct($"{m} Active"));
        var (_, inactive) = await CreateAsync(NewProduct($"{m} Inactive"));
        (await admin.PostAsJsonAsync($"{Products}/{inactive.Id}/deactivate", new { })).EnsureSuccessStatusCode();
        await factory.InsertAsync(Partner(ProductSources.Harbor, $"{m} Partner"));

        async Task<List<string>> Names(string query) =>
            [.. (await (await admin.GetAsync($"{Products}?{query}")).ReadAsync<PagedResult<AdminProductDto>>()).Items.Select(p => p.Name)];

        (await Names($"q={m}")).Should().Equal($"{m} Active", $"{m} Inactive");
        (await Names($"q={m}&status=active")).Should().Equal($"{m} Active");
        (await Names($"q={m}&status=inactive")).Should().Equal($"{m} Inactive");
        (await Names($"q={active.Sku.ToLowerInvariant()}")).Should().Equal($"{m} Active");
        (await admin.GetAsync($"{Products}?status=deleted")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Partner_products_are_not_editable_here()
    {
        var partner = Partner(ProductSources.Kestrel, $"{NewMarker()} Partner jacket");
        await factory.InsertAsync(partner);
        var admin = await factory.SignedInAsync(Roles.Admin);

        var get = await admin.GetAsync($"{Products}/{partner.Id}");
        var deactivate = await admin.PostAsJsonAsync($"{Products}/{partner.Id}/deactivate", new { });

        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        deactivate.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_own_media_library_lists_and_serves_the_images()
    {
        var admin = await factory.SignedInAsync(Roles.Admin);

        var library = await (await admin.GetAsync("/api/v1/admin/own-media")).ReadAsync<List<OwnMediaFileDto>>();
        var file = library.Should().ContainSingle(f => f.File == "lg-ap-001.png").Subject;
        var image = await admin.GetAsync(file.Url);

        file.Url.Should().Be("http://localhost/api/v1/admin/own-media/lg-ap-001.png");
        image.StatusCode.Should().Be(HttpStatusCode.OK);
        image.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await admin.GetAsync("/api/v1/admin/own-media/missing.png")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
