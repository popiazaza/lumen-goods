using System.Text.Json;

namespace HarborSupply.StandIn.Tests;

/// <summary>Contract: seed at least 40 items across all departments, 3 discontinued, 2 null-price, 5 LIMITED.</summary>
public sealed class SeedTests(HarborFactory factory) : IClassFixture<HarborFactory>
{
    private static readonly string[] Departments = ["BAGS", "TEES", "OUTERWEAR", "KITCHEN", "CAMP", "MISC"];

    [Fact]
    [Trait("CT", "CT-H-12")]
    public async Task CT_H_12_seed_meets_the_contract_minimums()
    {
        using var client = factory.CreateKeyedClient();

        var (items, _) = await client.GetAllPagesAsync();

        var limitedOnHand = items.Where(i => Status(i) == "LIMITED")
            .Select(i => i.GetProperty("inventory").GetProperty("on_hand").GetInt32())
            .ToList();

        items.Should().HaveCountGreaterThanOrEqualTo(40);
        items.Select(i => i.GetProperty("dept").GetString()).Distinct().Should().BeEquivalentTo(Departments);
        items.Count(i => i.GetProperty("discontinued").GetBoolean()).Should().BeGreaterThanOrEqualTo(3);
        items.Count(i => i.GetProperty("unit_price_cents").ValueKind == JsonValueKind.Null).Should().BeGreaterThanOrEqualTo(2);
        limitedOnHand.Should().HaveCountGreaterThanOrEqualTo(5);
        limitedOnHand.Should().Contain(n => n <= 5).And.Contain(n => n > 5, "both sides of Lumen's low-stock threshold are exercised");
        items.Should().Contain(i => Status(i) == "SOLD_OUT").And.Contain(i => Status(i) == "AVAILABLE");
        items.Select(i => i.GetProperty("updated_at").GetString()).Distinct().Should().HaveCountGreaterThan(20);
        items.SelectMany(i => i.GetProperty("props").EnumerateArray()).Select(p => p.GetProperty("name").GetString())
            .Distinct().Should().Contain(["colour", "size", "material", "capacity"]);
    }

    private static string Status(JsonElement item) => item.GetProperty("inventory").GetProperty("status").GetString()!;
}
