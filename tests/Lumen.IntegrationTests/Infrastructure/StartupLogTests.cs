using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Lumen.IntegrationTests.Infrastructure;

/// <summary>
/// The first <c>docker compose up</c> starts the API on an empty database. That start must log no errors, so a reviewer
/// reading <c>docker compose logs api</c> sees only real problems.
/// </summary>
public sealed class StartupLogTests(LumenApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrating_and_seeding_an_empty_database_logs_no_errors()
    {
        var database = $"fresh_{Guid.NewGuid():N}";
        await using (var server = new NpgsqlConnection(factory.DatabaseConnectionString))
        {
            await server.OpenAsync(Ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", server);
            await create.ExecuteNonQueryAsync(Ct);
        }

        var errors = new ErrorLog();
        var connectionString = new NpgsqlConnectionStringBuilder(factory.DatabaseConnectionString) { Database = database }.ConnectionString;
        await using var api = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Lumen", connectionString);
            builder.ConfigureLogging(logging => logging.AddProvider(errors));
        });

        // Starting the server runs Program.cs up to app.Run(): migrations and seeding included.
        _ = api.Server;

        errors.Entries.Should().BeEmpty();
    }

    /// <summary>Keeps every Error and Critical entry, with its category.</summary>
    private sealed class ErrorLog : ILoggerProvider
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    entries.Enqueue($"{category} ({eventId.Id}): {formatter(state, exception)}");
                }
            }
        }
    }
}
