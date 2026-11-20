using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace KestrelOutfitters.StandIn.Tests;

/// <summary>
/// The stand-in in memory, on its own temporary SQLite file, with a fake clock, no feed latency and
/// a configurable 503 rate, so every quirk is deterministic and no test sleeps.
/// </summary>
public class KestrelStandInFactory : WebApplicationFactory<Program>
{
    public const string Username = "lumen";
    public const string Password = "kestrel-secret";

    public string DatabasePath { get; } =
        Path.Combine(Path.GetTempPath(), $"kestrel-standin-tests-{Guid.NewGuid():N}.db");

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));

    protected virtual double FeedFailureRate => 0;

    protected virtual TimeSpan FeedLatency => TimeSpan.Zero;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KestrelOutfitters:DatabasePath"] = DatabasePath,
            ["KestrelOutfitters:FeedLatencyMin"] = FeedLatency.ToString("c"),
            ["KestrelOutfitters:FeedLatencyMax"] = FeedLatency.ToString("c"),
            ["KestrelOutfitters:FeedFailureRate"] = FeedFailureRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["KestrelOutfitters:RandomSeed"] = "42",
        }));
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = BasicAuth(Username, Password);
        return client;
    }

    public static AuthenticationHeaderValue BasicAuth(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    /// <summary>Runs a CLI subcommand against this instance's database, as <c>docker compose exec</c> would.</summary>
    public async Task<(int ExitCode, string Output)> RunCliAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await Cli.RunAsync(args, DatabasePath, Time, stdout, stderr, TestContext.Current.CancellationToken);
        return (exitCode, stdout.ToString() + stderr);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(DatabasePath);
    }
}

public sealed class AlwaysFailingFeedFactory : KestrelStandInFactory
{
    protected override double FeedFailureRate => 1;
}

public sealed class SlowFeedFactory : KestrelStandInFactory
{
    protected override TimeSpan FeedLatency => TimeSpan.FromSeconds(3);
}
