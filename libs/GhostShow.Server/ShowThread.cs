namespace GhostShow.Server;

/// <summary>
/// The thread the show is edited and read on. In the desktop app it's the UI thread (it hosts the server in-process
/// for now); a server on its own will have its own.
/// </summary>
public interface IShowThread
{
    /// <summary>Runs this on the show's thread, after what's already waiting.</summary>
    void Post(Action action);
}

/// <summary>Runs an action on the show's thread once calls have stopped for a while (a volume drag is saved once it settles).</summary>
public sealed class Debouncer(IShowThread thread, TimeSpan delay, Action action) : IDisposable
{
    readonly Lock _lock = new();
    Timer? _timer;

    /// <summary>Starts (or restarts) the wait.</summary>
    public void Poke() => Poke(delay);

    /// <summary>Starts (or restarts) the wait, this long this time.</summary>
    public void Poke(TimeSpan wait)
    {
        lock (_lock)
        {
            _timer ??= new Timer(_ => thread.Post(action));
            _timer.Change(wait, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Stops waiting (an action already on its way to the show's thread still runs).</summary>
    public void Cancel()
    {
        lock (_lock) _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        lock (_lock) _timer?.Dispose();
    }
}
