using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Lumen.IntegrationTests.Infrastructure;

/// <summary>
/// Keeps every log record a test host writes, at every level: the formatted message, the structured values and the
/// exception text, so a test can check what would reach the real log sinks.
/// </summary>
public sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _records = new();

    public IReadOnlyCollection<string> Records => _records;

    /// <summary>Adds it to a host's logging and lets every level through to it, whatever the configured minimum.</summary>
    public void AddTo(ILoggingBuilder logging)
    {
        logging.AddProvider(this);
        logging.AddFilter<LogCapture>(null, LogLevel.Trace);
    }

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _records);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<string> records) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join("; ", pairs.Select(p => $"{p.Key}={p.Value}"))
                : "";
            records.Enqueue($"{logLevel} {category}: {formatter(state, exception)} | {values} | {exception}");
        }
    }
}
