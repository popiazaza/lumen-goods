using System.Net;
using HarborSupply.StandIn.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static HarborSupply.StandIn.Tests.HttpTestExtensions;

namespace HarborSupply.StandIn.Tests;

/// <summary>Contract: every request needs X-Harbor-Key; every 4xx/5xx uses the standard error body.</summary>
public sealed class AuthAndErrorTests(HarborFactory factory) : IClassFixture<HarborFactory>
{
    [Theory]
    [Trait("CT", "CT-H-01")]
    [InlineData(null)]
    [InlineData("wrong-key")]
    [InlineData("HARBOR-DEV-KEY-123")]
    public async Task CT_H_01_missing_or_wrong_key_returns_401_with_the_standard_error_body(string? key)
    {
        using var client = factory.CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Harbor-Key", key);
        }

        using var list = await client.GetAsync("/v2/catalog/items", Ct);
        using var item = await client.GetAsync("/v2/catalog/items/HSC-10021", Ct);
        using var order = await client.PostOrderAsync("ref-unauthorised", ("HSC-10021", 1));

        await list.ShouldBeHarborErrorAsync(401, "UNAUTHORIZED");
        await item.ShouldBeHarborErrorAsync(401, "UNAUTHORIZED");
        await order.ShouldBeHarborErrorAsync(401, "UNAUTHORIZED");
    }

    [Fact]
    [Trait("CT", "CT-H-10")]
    public async Task CT_H_10_framework_errors_use_the_standard_body_never_problem_details()
    {
        using var client = factory.CreateKeyedClient();

        using var unknownRoute = await client.GetAsync("/v2/nothing-here", Ct);
        using var wrongMethod = await client.DeleteAsync("/v2/catalog/items/HSC-10021", Ct);
        using var unknownItem = await client.GetAsync("/v2/catalog/items/HSC-99999", Ct);

        await unknownRoute.ShouldBeHarborErrorAsync(404, "NOT_FOUND");
        await wrongMethod.ShouldBeHarborErrorAsync(405, "METHOD_NOT_ALLOWED");
        await unknownItem.ShouldBeHarborErrorAsync(404, "ITEM_NOT_FOUND");
    }

    [Fact]
    [Trait("CT", "CT-H-10")]
    public async Task CT_H_10_unhandled_exception_returns_500_with_the_standard_body()
    {
        await using var broken = new HarborFactory();
        using var client = broken.CreateKeyedClient();
        await using (var db = await broken.Services.GetRequiredService<IDbContextFactory<HarborDbContext>>().CreateDbContextAsync(Ct))
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE Items", Ct);
        }

        using var response = await client.GetAsync("/v2/catalog/items", Ct);

        var error = await response.ShouldBeHarborErrorAsync(500, "INTERNAL_ERROR");
        error.GetProperty("message").GetString().Should().NotContain("Items", "internal details must not leak");
    }
}
