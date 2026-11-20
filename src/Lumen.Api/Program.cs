using Lumen.Api.Identity;
using Lumen.Api.Infrastructure;
using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Infrastructure.Security;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.AddValidation();
builder.Services.AddOpenApi();
builder.Services.AddLumenData(builder.Configuration);
builder.Services.AddLumenIdentity(builder.Configuration);
builder.Services.AddLumenHealthChecks();
builder.Services.AddReverseProxySupport();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

// Every state-changing call under /api/v1 must carry the XSRF header (see AntiforgeryValidationFilter).
var api = app.MapGroup("/api/v1").AddEndpointFilter<AntiforgeryValidationFilter>();
api.MapAuthEndpoints();

app.MapHealthChecks("/health");

if (app.Configuration.GetValue("Docs:Enabled", app.Environment.IsDevelopment()))
{
    app.MapOpenApi("/api/openapi/{documentName}.json");
    app.MapScalarApiReference("/api/docs", options => options.WithOpenApiRoutePattern("/api/openapi/{documentName}.json"));
}

await app.Services.InitializeDatabaseAsync(app.Lifetime.ApplicationStopping);

app.Run();

// Exposed for WebApplicationFactory in the integration tests.
public partial class Program;
