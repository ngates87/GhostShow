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

public static class ShowThreadExtensions
{
    /// <summary>
    /// Runs a function on the show's thread and returns what it returns (or throws what it throws). Whoever awaits it
    /// carries on off that thread, so a device's connection never runs on the show's thread.
    /// </summary>
    public static Task<T> InvokeAsync<T>(this IShowThread thread, Func<T> function)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread.Post(() =>
        {
            try
            {
                done.SetResult(function());
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        });
        return done.Task;
    }
}

/// <summary>
/// Runs an action on the show's thread at an interval, for a host with no refresh timer of its own. Never more than one
/// waits: while the thread is busy, ticks that fall due are skipped rather than queued up behind it.
/// </summary>
public sealed class ShowHeartbeat : IDisposable
{
    readonly IShowThread _thread;
    readonly Action _action;
    readonly Timer _timer;
    int _waiting;   // 1 while a tick is posted and hasn't finished

    public ShowHeartbeat(IShowThread thread, TimeSpan interval, Action action)
    {
        _thread = thread;
        _action = action;
        _timer = new Timer(_ => Due(), null, interval, interval);
    }

    /// <summary>The interval is up: a tick is posted, unless the last one is still waiting or running.</summary>
    internal void Due()
    {
        if (Interlocked.Exchange(ref _waiting, 1) == 1) return;
        _thread.Post(() =>
        {
            try
            {
                _action();
            }
            finally
            {
                Volatile.Write(ref _waiting, 0);
            }
        });
    }

    public void Dispose() => _timer.Dispose();
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
