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
}
