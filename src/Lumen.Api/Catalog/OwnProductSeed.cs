using Lumen.Api.Infrastructure.Data;
using Lumen.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Catalog;

/// <summary>
/// Lumen Goods' own demo products, created on first run when <see cref="SeedOptions.Enabled"/> is on. Idempotent by
/// SKU: a product that already exists (or was edited in the admin pages) is never touched again. The set covers every
/// category and the cases the storefront must show: low stock, out of stock, an inactive product and one without
/// images.
/// </summary>
internal static class OwnProductSeed
{
    private sealed record SeedProduct(
        string Sku,
        string Name,
        string Category,
        decimal Price,
        int OnHand,
        int DaysAgo,
        string Description,
        Dictionary<string, string> Attributes,
        string[] Images,
        bool IsActive = true);

    private static readonly SeedProduct[] Products =
    [
        new("LG-AP-001", "Everyday Organic Cotton Tee", ProductCategories.Apparel, 28.00m, 42, 40,
            "A midweight crew-neck tee in certified organic cotton. Pre-washed for a soft hand and minimal shrinkage, with a relaxed fit that layers well.",
            new() { ["color"] = "Oat", ["material"] = "Organic cotton", ["fit"] = "Relaxed", ["sizeRange"] = "XS-XXL" },
            ["lg-ap-001.png", "lg-ap-001-b.png"]),
        new("LG-AP-002", "Merino Crew Sweater", ProductCategories.Apparel, 89.50m, 3, 33,
            "Fine-gauge merino wool knit that regulates temperature and resists odor. Ribbed cuffs and hem keep their shape wash after wash.",
            new() { ["color"] = "Navy", ["material"] = "Merino wool", ["fit"] = "Regular" },
            ["lg-ap-002.png"]),
        new("LG-AP-003", "Linen Button-Down Shirt", ProductCategories.Apparel, 64.00m, 0, 27,
            "Breathable European linen with a button-down collar and a straight hem you can wear untucked. Softens with every wash.",
            new() { ["color"] = "Sage", ["material"] = "Linen", ["fit"] = "Regular" },
            ["lg-ap-003.png"]),
        new("LG-AP-004", "Fleece Quarter-Zip Pullover", ProductCategories.Apparel, 72.25m, 18, 12,
            "Recycled polyester fleece with a stand-up collar and a metal quarter zip. Warm enough for cool mornings, light enough to pack.",
            new() { ["color"] = "Charcoal", ["material"] = "Recycled polyester fleece", ["fit"] = "Relaxed" },
            ["lg-ap-004.png"]),

        new("LG-FW-001", "Canvas Low-Top Sneakers", ProductCategories.Footwear, 59.99m, 25, 38,
            "Classic low-tops in heavy cotton canvas on a vulcanized natural rubber sole, with a cushioned insole and cotton laces.",
            new() { ["color"] = "Chalk", ["material"] = "Cotton canvas", ["sole"] = "Natural rubber" },
            ["lg-fw-001.png"]),
        new("LG-FW-002", "Suede Desert Boots", ProductCategories.Footwear, 129.00m, 4, 30,
            "Ankle-high desert boots in soft calf suede on a crepe rubber sole. Two-eyelet lacing and an unlined build that molds to your foot.",
            new() { ["color"] = "Tan", ["material"] = "Calf suede", ["sole"] = "Crepe rubber" },
            ["lg-fw-002.png"]),
        new("LG-FW-003", "Wool Lounge Slippers", ProductCategories.Footwear, 39.95m, 0, 21,
            "Boiled-wool slippers with a felt lining and a grippy suede sole, made for cold floors and slow mornings.",
            new() { ["color"] = "Heather gray", ["material"] = "Boiled wool", ["sole"] = "Suede" },
            ["lg-fw-003.png"]),
        new("LG-FW-004", "Trail Running Shoes", ProductCategories.Footwear, 114.50m, 7, 55,
            "Lightweight trail runners with a lugged outsole and a rock plate. Discontinued and replaced by next season's model.",
            new() { ["color"] = "Ember", ["material"] = "Recycled mesh", ["sole"] = "Lugged rubber" },
            ["lg-fw-004.png"], IsActive: false),

        new("LG-BG-001", "Waxed Canvas Weekender", ProductCategories.Bags, 148.00m, 9, 36,
            "A roomy weekend bag in water-resistant waxed cotton canvas with full-grain leather handles. Fits three days of clothes and a pair of shoes.",
            new() { ["color"] = "Olive", ["material"] = "Waxed canvas", ["capacity"] = "40 L" },
            ["lg-bg-001.png", "lg-bg-001-b.png"]),
        new("LG-BG-002", "Recycled Nylon Daypack", ProductCategories.Bags, 79.00m, 2, 18,
            "Everyday backpack in recycled ripstop nylon with a padded 15-inch laptop sleeve and a quick-access front pocket.",
            new() { ["color"] = "Black", ["material"] = "Recycled nylon", ["capacity"] = "20 L" },
            ["lg-bg-002.png"]),
        new("LG-BG-003", "Cotton Market Tote", ProductCategories.Bags, 18.50m, 120, 45,
            "Sturdy 12 oz cotton tote with long handles that fit over the shoulder. Folds flat into a pocket.",
            new() { ["color"] = "Natural", ["material"] = "Cotton canvas" },
            ["lg-bg-003.png"]),
        new("LG-BG-004", "Leather Crossbody Bag", ProductCategories.Bags, 112.00m, 5, 9,
            "Compact crossbody in vegetable-tanned leather with an adjustable strap and a brass snap. Holds a phone, wallet and keys.",
            new() { ["color"] = "Cognac", ["material"] = "Vegetable-tanned leather" },
            ["lg-bg-004.png"]),

        new("LG-HM-001", "Stoneware Coffee Mug", ProductCategories.Home, 22.00m, 64, 42,
            "Hand-glazed stoneware mug with a comfortable handle. Dishwasher and microwave safe.",
            new() { ["color"] = "Sand", ["material"] = "Stoneware", ["capacity"] = "12 oz" },
            ["lg-hm-001.png"]),
        new("LG-HM-002", "Linen Throw Blanket", ProductCategories.Home, 96.00m, 1, 15,
            "Stonewashed linen throw with fringed edges: light enough for summer evenings, and a warm extra layer on the bed in winter.",
            new() { ["color"] = "Terracotta", ["material"] = "Linen", ["dimensions"] = "50 x 70 in" },
            ["lg-hm-002.png"]),
        new("LG-HM-003", "Beeswax Taper Candles, Set of 4", ProductCategories.Home, 26.75m, 33, 6,
            "Hand-dipped pure beeswax tapers that burn clean and slow, about 8 hours each. Fit standard candle holders.",
            new() { ["material"] = "Beeswax", ["burnTime"] = "8 hours", ["count"] = "4" },
            []),
        new("LG-HM-004", "Walnut Serving Board", ProductCategories.Home, 54.00m, 12, 24,
            "Solid American black walnut, oiled by hand, with a carry handle. Large enough for a cheese spread for six.",
            new() { ["material"] = "Black walnut", ["dimensions"] = "18 x 9 in" },
            ["lg-hm-004.png"]),

        new("LG-OD-001", "Insulated Steel Water Bottle", ProductCategories.Outdoor, 34.00m, 80, 50,
            "Double-wall vacuum-insulated stainless steel that keeps drinks cold for 24 hours or hot for 12. Leakproof cap with a carry loop.",
            new() { ["color"] = "Forest", ["material"] = "Stainless steel", ["capacity"] = "25 oz" },
            ["lg-od-001.png"]),
        new("LG-OD-002", "Packable Rain Shell", ProductCategories.Outdoor, 119.00m, 6, 11,
            "Waterproof, breathable 2.5-layer shell with taped seams and an adjustable hood. Packs into its own chest pocket.",
            new() { ["color"] = "Ochre", ["material"] = "Recycled nylon", ["waterproofRating"] = "10,000 mm" },
            ["lg-od-002.png"]),
        new("LG-OD-003", "Folding Camp Stool", ProductCategories.Outdoor, 45.50m, 0, 29,
            "Aluminum frame with a heavy canvas seat that folds flat in one motion. Holds up to 250 lb.",
            new() { ["color"] = "Brown", ["material"] = "Aluminum and canvas", ["maxLoad"] = "250 lb" },
            ["lg-od-003.png"]),
        new("LG-OD-004", "Wool Blend Camp Blanket", ProductCategories.Outdoor, 88.00m, 14, 3,
            "Heavy wool-blend blanket with a classic stripe and leather carry straps. Stays warm when damp, at home in the car or by the fire.",
            new() { ["color"] = "Red and cream", ["material"] = "Wool blend", ["dimensions"] = "60 x 80 in" },
            ["lg-od-004.png"]),

        new("LG-AC-001", "Leather Card Wallet", ProductCategories.Accessories, 45.00m, 27, 47,
            "Slim card wallet in full-grain leather with four card slots and a center pocket for folded bills.",
            new() { ["color"] = "Cognac", ["material"] = "Full-grain leather" },
            ["lg-ac-001.png"]),
        new("LG-AC-002", "Ribbed Merino Beanie", ProductCategories.Accessories, 32.00m, 5, 8,
            "Soft rib-knit merino beanie with a deep turn-up and a pom-pom. Warm, breathable and itch-free.",
            new() { ["color"] = "Rust", ["material"] = "Merino wool" },
            ["lg-ac-002.png"]),
        new("LG-AC-003", "Polarized Sunglasses", ProductCategories.Accessories, 78.00m, 11, 20,
            "Acetate frames with polarized UV400 lenses that cut glare on water and snow. Comes with a canvas case.",
            new() { ["color"] = "Tortoise", ["frameMaterial"] = "Acetate", ["lens"] = "Polarized UV400" },
            ["lg-ac-003.png"]),
        new("LG-AC-004", "Canvas Watch Strap", ProductCategories.Accessories, 24.99m, 2, 1,
            "Quick-release cotton canvas strap with a brushed steel buckle. Fits 20 mm lugs.",
            new() { ["color"] = "Olive", ["material"] = "Cotton canvas", ["width"] = "20 mm" },
            ["lg-ac-004.png"]),
    ];

    /// <summary>The seeded SKUs, for tests.</summary>
    public static IReadOnlyList<string> Skus { get; } = [.. Products.Select(p => p.Sku)];

    public static async Task SeedAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        if (!services.GetRequiredService<IOptions<SeedOptions>>().Value.Enabled)
        {
            return;
        }

        var db = services.GetRequiredService<LumenDbContext>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var existing = await db.Products
            .Where(p => p.Source == ProductSources.Lumen)
            .Select(p => p.SourceProductId)
            .ToListAsync(cancellationToken);

        var missing = 0;
        foreach (var (seed, index) in Products.Select((p, i) => (p, i)))
        {
            if (existing.Contains(seed.Sku))
            {
                continue;
            }

            missing++;

            // Spread over recent weeks so "newest" has something to sort; the hour keeps same-day products apart.
            var firstSeen = now.AddDays(-seed.DaysAgo).AddHours(-(index % 12));
            db.Products.Add(new Product
            {
                Source = ProductSources.Lumen,
                SourceProductId = seed.Sku,
                Name = seed.Name,
                Description = seed.Description,
                Category = seed.Category,
                PriceUsd = seed.Price,
                SourcePrice = seed.Price,
                SourceCurrency = "USD",
                OnHand = seed.OnHand,
                Reserved = 0,
                IsActive = seed.IsActive,
                IsListed = seed.IsActive,
                Images = [.. seed.Images.Select((file, position) => new ProductImage(OwnMediaLibrary.ToSourceUrl(file), IsPrimary: position == 0))],
                Attributes = seed.Attributes,
                FirstSeenUtc = firstSeen,
                LastUpdatedUtc = firstSeen.AddDays(Math.Min(seed.DaysAgo, seed.DaysAgo % 4)),
            });
        }

        if (missing > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Seeded {Count} own products", missing);
        }
    }
}
