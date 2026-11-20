using System.Globalization;

namespace HarborSupply.StandIn.Persistence;

/// <summary>
/// First-run catalogue: 48 items, 8 per department. The contract asks for at least 40 items across all departments,
/// 3 discontinued, 2 with a null price and 5 LIMITED. This seed has 5 discontinued, 3 price-on-request,
/// 13 LIMITED (on hand 1-5 and 6-10, both sides of Lumen's low-stock line), 6 sold out, one item with no images,
/// and three items sharing one updated_at so paging has to break ties on item_code.
/// HSC-10021 is the spec's sample item, field for field.
/// </summary>
internal static class SeedData
{
    public static IEnumerable<CatalogItem> Items() =>
    [
        // BAGS
        Item("HSC-10021", "BAGS", "Waxed Canvas Tote", "Heavy waxed canvas tote with leather handles.", 4999, 3, "2026-10-02T09:15:00Z", "front,side", "colour=Olive"),
        Item("HSC-10022", "BAGS", "Rolltop Daypack", "Weatherproof rolltop pack with a padded laptop sleeve.", 8900, 42, "2026-09-03T14:20:00Z", "front,side,detail", "colour=Slate; capacity=24 L; material=Recycled nylon"),
        Item("HSC-10023", "BAGS", "Leather Field Satchel", "Full-grain leather satchel with brass buckles that darkens with age.", 18900, 7, "2026-10-21T16:45:00Z", "front,side", "colour=Tan; material=Full-grain leather"),
        Item("HSC-10024", "BAGS", "Canvas Duffel 45L", "Roomy cotton canvas duffel with a separate shoe compartment.", 11500, 0, "2026-11-12T07:30:00Z", "front", "colour=Navy; capacity=45 L; material=Cotton canvas"),
        Item("HSC-10025", "BAGS", "Packable Market Bag", "Ripstop shopping bag that folds into its own pocket.", 1499, 120, "2026-09-08T11:00:00Z", "front", "colour=Rust; material=Ripstop nylon"),
        Item("HSC-10026", "BAGS", "Harbor Wash Bag", "Waxed canvas wash bag with a water-resistant lining.", 2450, 35, "2026-09-19T10:10:00Z", "front,side", "colour=Charcoal; material=Waxed canvas"),
        Item("HSC-10027", "BAGS", "Expedition Roller Case", "Hard-wearing roller case built to order for expeditions.", null, 4, "2026-10-28T13:05:00Z", "front,side", "colour=Black; capacity=90 L"),
        Item("HSC-10028", "BAGS", "Waxed Utility Belt Bag", "Compact hip bag with two zip pockets.", 3200, 18, "2026-11-03T09:00:00Z", "front", "colour=Olive; material=Waxed canvas", discontinued: true),

        // TEES
        Item("HSC-20011", "TEES", "Heavyweight Pocket Tee", "Boxy 240 gsm organic cotton tee with a chest pocket.", 2800, 64, "2026-10-15T08:00:00Z", "front,side", "colour=Bone; size=M; material=Organic cotton"),
        Item("HSC-20012", "TEES", "Slub Crew Tee", "Soft slub-knit crew neck with a relaxed fit.", 2400, 9, "2026-09-25T15:40:00Z", "front", "colour=Sage; size=L; material=Cotton"),
        Item("HSC-20013", "TEES", "Harbor Logo Tee", "Classic fit tee with a screen-printed lighthouse logo.", 2200, 150, "2026-09-01T09:00:00Z", "front,side", "colour=Navy; size=S"),
        Item("HSC-20014", "TEES", "Merino Base Tee", "Lightweight merino tee for hiking and layering.", 6500, 2, "2026-11-17T18:25:00Z", "front,side,detail", "colour=Charcoal; size=M; material=Merino wool"),
        Item("HSC-20015", "TEES", "Long Sleeve Henley", "Waffle-knit henley with a three-button placket.", 3800, 0, "2026-10-09T12:00:00Z", "front", "colour=Oatmeal; size=XL; material=Cotton waffle"),
        Item("HSC-20016", "TEES", "Sun Hoodie", "UPF 50 hooded top for long days on the water.", 4400, 27, "2026-10-30T10:30:00Z", "front,side", "colour=Sky; size=M; material=Recycled polyester"),
        Item("HSC-20017", "TEES", "Striped Breton Tee", "Heavy cotton Breton stripe with a boat neck.", 3400, 14, "2026-09-12T08:45:00Z", "front", "colour=Navy/White; size=S", discontinued: true),
        Item("HSC-20018", "TEES", "Garment-Dyed Tee", "Pigment-dyed tee with a lived-in look from day one.", 2600, 55, "2026-11-06T14:15:00Z", "front,side", "colour=Clay; size=L"),

        // OUTERWEAR
        Item("HSC-30031", "OUTERWEAR", "Waxed Cotton Field Jacket", "Four-pocket field jacket in waxed cotton with a corduroy collar.", 22900, 6, "2026-10-18T09:50:00Z", "front,side,detail", "colour=Olive; size=M; material=Waxed cotton"),
        Item("HSC-30032", "OUTERWEAR", "Packable Rain Shell", "Seam-sealed shell that packs into its own hood.", 12900, 31, "2026-09-29T17:00:00Z", "front,side", "colour=Yellow; size=L; material=2.5-layer nylon"),
        Item("HSC-30033", "OUTERWEAR", "Quilted Liner Vest", "Diamond-quilted vest that layers under a shell.", 7900, 22, "2026-11-01T06:00:00Z", "front", "colour=Black; size=M"),
        Item("HSC-30034", "OUTERWEAR", "Wool Chore Coat", "Boxy chore coat in a dense wool blend.", 18500, 0, "2026-11-14T11:20:00Z", "front,side", "colour=Charcoal; size=L; material=Wool blend"),
        Item("HSC-30035", "OUTERWEAR", "Down Parka", "Hip-length parka with a storm hood and fleece-lined pockets.", 32900, 12, "2026-10-05T13:35:00Z", "front,side", "colour=Forest; size=M; material=700-fill down"),
        Item("HSC-30036", "OUTERWEAR", "Fleece Pullover", "Recycled fleece quarter-zip with a snap chest pocket.", 6900, 48, "2026-09-16T10:00:00Z", "front", "colour=Heather Grey; size=S; material=Recycled fleece"),
        Item("HSC-30037", "OUTERWEAR", "Storm Poncho", "Oversized poncho that covers you and your pack.", 5400, 1, "2026-11-18T08:10:00Z", "front", "colour=Orange; size=One size"),
        Item("HSC-30038", "OUTERWEAR", "Softshell Jacket", "Stretch softshell with a brushed lining.", 9900, 25, "2026-09-22T12:40:00Z", "front,side", "colour=Blue; size=L", discontinued: true),

        // KITCHEN
        Item("HSC-40041", "KITCHEN", "Enamel Camp Mug", "Speckled enamel mug that goes from campfire to kitchen table.", 1600, 200, "2026-10-15T08:00:00Z", "front", "colour=White; capacity=350 ml; material=Enamelled steel"),
        Item("HSC-40042", "KITCHEN", "Cast Iron Skillet", "Pre-seasoned cast iron skillet with a helper handle.", 4500, 8, "2026-10-11T15:00:00Z", "front,side", "size=10 in; material=Cast iron"),
        Item("HSC-40043", "KITCHEN", "Pour-Over Coffee Set", "Stainless dripper, carafe and filters for two cups.", 5800, 19, "2026-09-05T07:15:00Z", "front,side", "capacity=600 ml; material=Stainless steel"),
        Item("HSC-40044", "KITCHEN", "Linen Tea Towels, Set of 3", "Stonewashed linen towels that dry fast and soften with use.", 2200, 75, "2026-10-24T09:30:00Z", "front", "colour=Natural; material=Linen"),
        Item("HSC-40045", "KITCHEN", "Walnut Serving Board", "End-grain walnut board finished with food-safe oil.", 6400, 0, "2026-11-09T16:00:00Z", "front,side", "size=45 x 25 cm; material=Walnut"),
        Item("HSC-40046", "KITCHEN", "Stoneware Bowl Set", "Four hand-glazed stoneware bowls.", 4800, 5, "2026-09-27T11:45:00Z", "front,side", "colour=Speckled White; material=Stoneware"),
        Item("HSC-40047", "KITCHEN", "Copper Kettle", "Polished copper stovetop kettle with a whistle.", 8900, 16, "2026-10-07T08:20:00Z", "front", "capacity=1.5 L; material=Copper"),
        Item("HSC-40048", "KITCHEN", "Engraved Knife Roll", "Leather knife roll with custom engraving, quoted per order.", null, 20, "2026-11-04T10:00:00Z", "front,side", "colour=Brown; material=Leather"),

        // CAMP
        Item("HSC-50051", "CAMP", "Two-Person Trail Tent", "Freestanding tent with two doors and two vestibules.", 34900, 10, "2026-10-13T12:00:00Z", "front,side,detail", "capacity=2 person; material=Ripstop nylon"),
        Item("HSC-50052", "CAMP", "Down Sleeping Bag", "Mummy bag rated to -5 C.", 27900, 13, "2026-09-10T09:25:00Z", "front,side", "colour=Red; material=650-fill down"),
        Item("HSC-50053", "CAMP", "Folding Camp Chair", "Packable chair with a steel frame and side pocket.", 5900, 44, "2026-10-26T14:50:00Z", "front", "colour=Green"),
        Item("HSC-50054", "CAMP", "LED Lantern", "Rechargeable lantern with warm and cool light modes.", 3900, 0, "2026-11-11T19:00:00Z", "front", "capacity=4000 mAh"),
        Item("HSC-50055", "CAMP", "Titanium Cook Pot", "Ultralight pot with a folding handle and lid.", 4900, 3, "2026-10-20T07:55:00Z", "front,side", "capacity=900 ml; material=Titanium"),
        Item("HSC-50056", "CAMP", "Insulated Sleeping Pad", "Inflatable pad with an R-value of 4.2.", 12900, 28, "2026-09-14T13:10:00Z", "front", "size=Regular"),
        Item("HSC-50057", "CAMP", "Four-Season Basecamp Tent", "Expedition dome for six, quoted on request.", null, 2, "2026-11-08T09:40:00Z", "front,side", "capacity=6 person; material=Polyester"),
        Item("HSC-50058", "CAMP", "Canvas Camp Stool", "Folding beech and canvas stool.", 4200, 11, "2026-09-20T16:30:00Z", "front", "material=Beech and canvas", discontinued: true),

        // MISC
        Item("HSC-60061", "MISC", "Brass Carabiner Keyring", "Solid brass keyring with a spring gate.", 1800, 90, "2026-10-15T08:00:00Z", "front", "material=Solid brass"),
        Item("HSC-60062", "MISC", "Wool Beanie", "Ribbed lambswool beanie with a fold-over cuff.", 2900, 4, "2026-11-15T10:05:00Z", "front,side", "colour=Mustard; size=One size; material=Lambswool"),
        Item("HSC-60063", "MISC", "Leather Card Wallet", "Slim vegetable-tanned wallet for six cards.", 3900, 37, "2026-09-30T12:25:00Z", "front,side", "colour=Brown; material=Leather"),
        Item("HSC-60064", "MISC", "Polarised Sunglasses", "Acetate frames with polarised lenses.", 7500, 23, "2026-10-03T15:15:00Z", "front,side", "colour=Tortoise"),
        Item("HSC-60065", "MISC", "Enamel Pin Set", "Three enamel pins with coastal motifs.", 1200, 60, "2026-10-31T11:00:00Z", "", "colour=Multi"),
        Item("HSC-60066", "MISC", "Notebook Cover", "Waxed canvas notebook cover with a pen loop.", 3500, 15, "2026-09-07T10:50:00Z", "front", "colour=Olive; size=A5"),
        Item("HSC-60067", "MISC", "Bandana Twin Pack", "Two cotton bandanas in a paisley print.", 1600, 70, "2026-11-02T13:30:00Z", "front", "colour=Indigo; material=Cotton"),
        Item("HSC-60068", "MISC", "Paracord Bracelet", "Survival bracelet with three metres of paracord.", 1400, 0, "2026-09-18T08:05:00Z", "front", "colour=Coyote", discontinued: true),
    ];

    /// <param name="views">Comma-separated image views; the first is primary. Empty for an item without images.</param>
    /// <param name="props">"name=value" pairs separated by semicolons.</param>
    private static CatalogItem Item(
        string code, string dept, string title, string summary, int? priceCents, int onHand, string updatedAt,
        string views, string props, bool discontinued = false) =>
        CatalogItem.Create(
            code,
            dept,
            title,
            summary,
            priceCents,
            onHand,
            discontinued,
            DateTime.Parse(updatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            [.. views.Split(',', StringSplitOptions.RemoveEmptyEntries)],
            [.. props.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .Select(p => new ItemProp { Name = p[0], Value = p[1] })]);
}
