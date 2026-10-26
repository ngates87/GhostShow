using Microsoft.Extensions.Logging;

namespace GhostShow.Core.Diagnostics;

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message, string? Exception);

/// <summary>
/// An ILoggerProvider that keeps the most recent warnings and errors in memory so the UI can list them.
/// Everything still goes to the other configured loggers (console, file); this is only the on-screen view.
/// </summary>
public sealed class RecentEventsLog : ILoggerProvider
{
    readonly Lock _lock = new();
    readonly LogEntry[] _entries;
    int _count, _next;

    public RecentEventsLog(int capacity = 200, LogLevel minimumLevel = LogLevel.Information)
    {
        _entries = new LogEntry[capacity];
        MinimumLevel = minimumLevel;
    }

    public LogLevel MinimumLevel { get; }

    /// <summary>Raised on the thread that logged. UI subscribers must marshal to their own thread.</summary>
    public event Action<LogEntry>? EntryAdded;

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_lock)
        {
            var result = new LogEntry[_count];
            int oldest = (_next - _count + _entries.Length) % _entries.Length;
            for (int i = 0; i < _count; i++) result[i] = _entries[(oldest + i) % _entries.Length];
            return result;
        }
    }

    public void Add(LogEntry entry)
    {
        lock (_lock)
        {
            _entries[_next] = entry;
            _next = (_next + 1) % _entries.Length;
            _count = Math.Min(_count + 1, _entries.Length);
        }
        EntryAdded?.Invoke(entry);
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    sealed class Logger(RecentEventsLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= log.MinimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            log.Add(new LogEntry(DateTimeOffset.Now, logLevel, category, formatter(state, exception), exception?.Message));
        }
    }
}
