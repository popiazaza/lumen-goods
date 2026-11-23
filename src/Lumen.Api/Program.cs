using Lumen.Api.Carts;
using Lumen.Api.Catalog;
using Lumen.Api.Checkout;
using Lumen.Api.Identity;
using Lumen.Api.Infrastructure;
using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Infrastructure.Json;
using Lumen.Api.Infrastructure.OpenApi;
using Lumen.Api.Infrastructure.RateLimiting;
using Lumen.Api.Infrastructure.Security;
using Lumen.Api.Infrastructure.Telemetry;
using Lumen.Api.Orders;
using Lumen.Api.Partners;
using Lumen.Api.Payments;
using Lumen.Api.Wallets;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddLumenTelemetry(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options => JsonDefaults.Configure(options.SerializerOptions));
builder.Services.AddValidation();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<XmlDocTextTransformer>().AddSchemaTransformer<CartQuantitySchemaTransformer>());
builder.Services.AddLumenData(builder.Configuration);
builder.Services.AddLumenIdentity(builder.Configuration);
builder.Services.AddCatalog(builder.Configuration);
builder.Services.AddPartners(builder.Configuration);
builder.Services.AddCart(builder.Configuration);
builder.Services.AddCheckout(builder.Configuration);
builder.Services.AddPayments(builder.Configuration);
builder.Services.AddOrders(builder.Configuration);
builder.Services.AddWallet(builder.Configuration);
builder.Services.AddLumenHealthChecks();
builder.Services.AddReverseProxySupport(builder.Configuration);
builder.Services.AddRequestLimits(builder.Configuration);
builder.Services.AddLumenRateLimiting(builder.Configuration);

var app = builder.Build();

app.UseForwardedHeaders();
app.UseNoStoreForApi();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
// After authentication, so the per-customer limits know the customer; after forwarded headers, so per-IP limits see the client.
app.UseRateLimiter();

// Every state-changing call under /api/v1 must carry the XSRF header (see AntiforgeryValidationFilter).
var api = app.MapGroup("/api/v1").AddEndpointFilter<AntiforgeryValidationFilter>();
api.MapAuthEndpoints();
api.MapCatalogEndpoints();
api.MapPartnerAdminEndpoints();
api.MapCartEndpoints();
api.MapCheckoutEndpoints();
api.MapPaymentEndpoints();
api.MapOrderEndpoints();
api.MapAdminOrderEndpoints();
api.MapWalletEndpoints();

app.MapMediaEndpoints();
app.MapLumenHealthChecks();

if (app.Configuration.GetValue("Docs:Enabled", app.Environment.IsDevelopment()))
{
    app.MapOpenApi("/api/openapi/{documentName}.json");
    // No default fonts: Scalar would fetch them from its CDN, which the CSP (font-src 'self') blocks; system fonts it is.
    // No vendor chrome either: the "Ask AI" agent, MCP and telemetry call Scalar's services, and the developer tools
    // toolbar (deploy, SDK generation) advertises Scalar's products; this page documents Lumen's API and nothing else.
    app.MapScalarApiReference("/api/docs", options => options
        .WithOpenApiRoutePattern("/api/openapi/{documentName}.json")
        .DisableDefaultFonts()
        .DisableAgent()
        .DisableMcp()
        .DisableTelemetry()
        .HideDeveloperTools());
}

await app.Services.InitializeDatabaseAsync(app.Lifetime.ApplicationStopping);

app.Run();

// Exposed for WebApplicationFactory in the integration tests.
public partial class Program;
