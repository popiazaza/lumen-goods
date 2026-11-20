using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Lumen.IntegrationTests.Infrastructure.LumenApiFactory))]

namespace Lumen.IntegrationTests.Infrastructure;

/// <summary>
/// Runs the real API against a real PostgreSQL 18 container (same major as production), started once per
/// test assembly. Migrations and seeding run exactly as they do in docker compose.
/// </summary>
public sealed class LumenApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:18.7-alpine3.24").Build();

    public async ValueTask InitializeAsync()
    {
        await _database.StartAsync();

        // Start the API once, before any test runs. WebApplicationFactory builds its server lazily and not thread-safely,
        // so test classes running in parallel could otherwise start two hosts that migrate and seed at the same time.
        _ = Server;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _database.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Lumen", _database.GetConnectionString());
        // The test client talks plain HTTP, so cookies must not require HTTPS.
        builder.UseSetting("Auth:CookieSecurePolicy", "SameAsRequest");
        builder.UseSetting("Seed:Enabled", "true");
    }

    public ApiSession CreateSession() => new(CreateClient());
}
