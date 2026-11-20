using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Lumen.IntegrationTests.Infrastructure;

/// <summary>The OpenAPI document (served when Docs:Enabled) describes what the handlers return.</summary>
public sealed class OpenApiTests(LumenApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_document_names_the_success_bodies_of_place_order_and_the_payment_calls()
    {
        await using var app = factory.WithWebHostBuilder(builder => builder.UseSetting("Docs:Enabled", "true"));

        var paths = JsonDocument.Parse(await app.CreateClient().GetStringAsync("/api/openapi/v1.json", Ct)).RootElement.GetProperty("paths");

        var placeOrder = Responses(paths, "/api/v1/checkout/orders");
        SchemaOf(placeOrder, "201").Should().EndWith("/OrderDetailDto");
        SchemaOf(placeOrder, "200").Should().EndWith("/OrderDetailDto");
        SchemaOf(placeOrder, "409").Should().EndWith("/PriceChangedProblem");
        SchemaOf(Responses(paths, "/api/v1/orders/{number}/payments"), "202").Should().EndWith("/PaymentStatusDto");
        SchemaOf(Responses(paths, "/api/v1/orders/{number}/payments/current/one-time-code"), "200").Should().EndWith("/PaymentStatusDto");
    }

    private static JsonElement Responses(JsonElement paths, string path) =>
        paths.GetProperty(path).GetProperty("post").GetProperty("responses");

    private static string? SchemaOf(JsonElement responses, string status) =>
        responses.GetProperty(status).GetProperty("content").EnumerateObject().First().Value.GetProperty("schema").GetProperty("$ref").GetString();
}
