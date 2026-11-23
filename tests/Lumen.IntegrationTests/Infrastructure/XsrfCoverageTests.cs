using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lumen.Api.Checkout;
using Lumen.Api.Identity;
using Lumen.Api.Infrastructure.Json;
using Lumen.IntegrationTests.Orders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Lumen.IntegrationTests.Infrastructure;

/// <summary>
/// The XSRF check is one filter on the /api/v1 group (Program.cs), so a state-changing route mapped outside the group,
/// or a group that skips it, would go unnoticed by tests of single endpoints. This asks the app for every route it
/// has and sends each state-changing one without the X-XSRF-TOKEN header, signed in with the role the route needs.
/// </summary>
public sealed partial class XsrfCoverageTests(LumenApiFactory factory)
{
    private static readonly string[] StateChanging = [HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete];

    /// <summary>State-changing routes deliberately outside the check, with the reason. None today.</summary>
    private static readonly Dictionary<string, string> Exempt = [];

    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();

    /// <summary>
    /// Bodies that pass the built-in request validation, which runs before the XSRF filter: with an empty body these
    /// routes would answer a validation 400 and the test would prove nothing about XSRF. Every other route gets {}.
    /// </summary>
    private static readonly Dictionary<string, object> ValidBodies = new()
    {
        ["POST /api/v1/auth/register"] = new { email = $"xsrf{Guid.NewGuid():N}@example.test", password = "Shopper!2026" },
        ["POST /api/v1/auth/sign-in"] = new { email = $"xsrf{Guid.NewGuid():N}@example.test", password = "Shopper!2026" },
        ["POST /api/v1/cart/lines"] = new { productId = Guid.NewGuid(), quantity = 1 },
        ["PUT /api/v1/cart/lines/{lineId:guid}"] = new { quantity = 1 },
        ["POST /api/v1/checkout/orders"] = new
        {
            checkoutKey = Guid.NewGuid(), lines = new[] { new { productId = Guid.NewGuid(), quantity = 1, unitPrice = 1m } }, expectedTotal = 1m,
            shippingAddress = new ShippingAddressDto("Ada Lovelace", "1200 NW Naito Pkwy", null, "Portland", "OR", "97209", "US"),
            shippingMethod = "standard",
        },
        ["POST /api/v1/orders/{number}/payments/"] = new { method = "wallet" },
        ["POST /api/v1/orders/{number}/payments/current/one-time-code"] = new { code = "123456" },
        ["POST /api/v1/admin/orders/{number}/lines/{lineId:guid}/resolve"] = new { decision = "confirm" },
    };

    [GeneratedRegex(@"\{(?<name>[^}:=?]+)(?<constraints>[^}]*)\}")]
    private static partial Regex RouteParameter();

    private IReadOnlyList<(string Method, string Pattern, bool AdminOnly)> StateChangingRoutes() =>
        [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Where(m => StateChanging.Contains(m, StringComparer.OrdinalIgnoreCase))
                .Select(m => (Method: m, Pattern: "/" + e.RoutePattern.RawText!.TrimStart('/'),
                    AdminOnly: e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == Policies.Admin))))
            .Where(r => !Exempt.ContainsKey($"{r.Method} {r.Pattern}"))
            .Distinct()
            .OrderBy(r => r.Pattern, StringComparer.Ordinal).ThenBy(r => r.Method, StringComparer.Ordinal)];

    /// <summary>Route values that satisfy each parameter's constraints, so the request reaches the endpoint.</summary>
    private static string Url(string pattern) => RouteParameter().Replace(pattern, match =>
        match.Groups["constraints"].Value.Contains("guid", StringComparison.OrdinalIgnoreCase) ? Guid.NewGuid().ToString()
        : match.Groups["constraints"].Value.Contains("int", StringComparison.OrdinalIgnoreCase) ? "1"
        : "LG-XSRF-TEST");

    [Fact]
    public void Every_state_changing_route_is_under_the_api_group_that_checks_xsrf()
    {
        var routes = StateChangingRoutes();

        routes.Should().NotBeEmpty();
        routes.Where(r => !r.Pattern.StartsWith("/api/v1/", StringComparison.Ordinal)).Select(r => $"{r.Method} {r.Pattern}")
            .Should().BeEmpty("the XSRF filter is on the /api/v1 group; anything else must be listed in Exempt with a reason");
    }

    [Fact]
    public async Task Every_state_changing_endpoint_refuses_a_request_without_the_xsrf_header()
    {
        var customer = (await factory.SignedInCustomerAsync()).Session;
        var admin = await factory.SignedInAdminAsync();
        var routes = StateChangingRoutes();
        routes.Should().HaveCountGreaterThan(15, "the walk must find the API's routes");

        var answers = new List<string>();
        foreach (var (method, pattern, adminOnly) in routes)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), Url(pattern))
            {
                Content = JsonContent.Create(ValidBodies.GetValueOrDefault($"{method} {pattern}", new { }), options: Json),
            };
            var response = await (adminOnly ? admin : customer).Client.SendAsync(request, TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            if (response.StatusCode != HttpStatusCode.BadRequest || !body.Contains("Missing or invalid XSRF token", StringComparison.Ordinal))
            {
                answers.Add($"{method} {pattern} answered {(int)response.StatusCode}: {body[..Math.Min(body.Length, 200)]}");
            }
        }

        answers.Should().BeEmpty("every state-changing call without the XSRF header is refused before it does anything; answers:\n" + string.Join("\n", answers));
    }
}
