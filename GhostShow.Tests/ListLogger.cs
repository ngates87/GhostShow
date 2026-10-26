using Microsoft.Extensions.Logging;

namespace GhostShow.Tests;

/// <summary>Collects log messages so tests can check that failures are reported.</summary>
sealed class ListLogger : ILogger, ILoggerFactory
{
    readonly Lock _lock = new();
    readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get { lock (_lock) return _entries.ToArray(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lock) _entries.Add((logLevel, formatter(state, exception)));
    }

    public ILogger CreateLogger(string categoryName) => this;
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }
}
