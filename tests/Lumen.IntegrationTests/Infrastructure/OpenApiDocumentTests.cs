using System.Net;
using System.Text.Json;

namespace Lumen.IntegrationTests.Infrastructure;

/// <summary>The OpenAPI document behind Scalar (/api/docs) carries the API's own /// comments.</summary>
public sealed class OpenApiDocumentTests(LumenApiFactory factory)
{
    [Fact]
    public async Task The_document_describes_contracts_with_their_xml_comments()
    {
        using var response = await factory.CreateClient().GetAsync("/api/openapi/v1.json", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("AddCartLineRequest")
            .GetProperty("description").GetString().Should().Contain("add a product, or add to its existing line");
    }

    [Fact]
    public async Task The_document_reads_as_documentation_not_csharp()
    {
        using var response = await factory.CreateClient().GetAsync("/api/openapi/v1.json", TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        var texts = Texts(document.RootElement).ToList();
        texts.Should().NotContain(t => t.Contains("&lt;", StringComparison.Ordinal), "a cref's C# signature is not shown");
        texts.Should().NotContain(t => t.Contains("&amp;", StringComparison.Ordinal), "XML escapes are decoded");
        texts.Should().NotContain(t => t.Contains("PlaceOrderRequest.Lines", StringComparison.Ordinal));
        texts.Should().NotContain(t => t.Split('\n').Any(line => line.StartsWith("    ", StringComparison.Ordinal)),
            "four spaces would make Markdown show the text as code");

        var placeOrder = schemas.GetProperty("PlaceOrderRequest").GetProperty("description").GetString();
        placeOrder.Should().Contain("\n1. the cart's products against `lines`")
            .And.Contain("\n2. each line's").And.Contain("\n3. the re-priced");
        schemas.GetProperty("UpdateCartLineRequest").GetProperty("examples")[0].GetProperty("quantity").GetInt32().Should().Be(3);
        foreach (var request in new[] { "UpdateCartLineRequest", "AddCartLineRequest" })
        {
            // The quantity's lenient converter must not leave it described as "any value".
            var quantity = schemas.GetProperty(request).GetProperty("properties").GetProperty("quantity");
            (quantity.GetProperty("type").GetString(), quantity.GetProperty("minimum").GetInt32(), quantity.GetProperty("maximum").GetInt32())
                .Should().Be(("integer", 1, 10));
        }

        json.Should().NotContain("```").And.NotContain("\"examples\": [null]").And.NotContain("\"examples\":[null]");
    }

    private static IEnumerable<string> Texts(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "description" or "summary" && property.Value.ValueKind == JsonValueKind.String)
                {
                    yield return property.Value.GetString()!;
                }

                foreach (var text in Texts(property.Value))
                {
                    yield return text;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var text in element.EnumerateArray().SelectMany(Texts))
            {
                yield return text;
            }
        }
    }

    [Fact]
    public async Task Scalar_serves_the_reference_page_for_that_document()
    {
        using var response = await factory.CreateClient().GetAsync("/api/docs", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        page.Should().Contain("\"url\":\"api/openapi/v1.json\"")
            .And.Contain("\"withDefaultFonts\":false", "Scalar's CDN fonts would break the font-src 'self' CSP");
        // No vendor chrome: no "Ask AI" agent, no developer toolbar (deploy, SDK generation), no MCP, no telemetry.
        page.Should().Contain("\"showDeveloperTools\":\"never\"")
            .And.MatchRegex("\"agent\":\\{[^}]*\"disabled\":true")
            .And.MatchRegex("\"mcp\":\\{[^}]*\"disabled\":true")
            .And.Contain("\"telemetry\":false");
    }
}
