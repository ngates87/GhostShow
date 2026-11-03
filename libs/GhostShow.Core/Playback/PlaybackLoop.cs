using System.Diagnostics;
using GhostShow.Core.Diagnostics;
using GhostShow.Core.Timing;
using Microsoft.Extensions.Logging;

namespace GhostShow.Core.Playback;

/// <summary>
/// Runs <see cref="ShowPlayer.Tick"/> (or <see cref="PlaybackManager.Tick"/>) every 10 ms on a dedicated thread, independent of the UI.
///
/// Why a dedicated thread: playback timing must not depend on UI rendering or thread-pool load, and one thread with
/// a precise wait is simpler and steadier than timers. 100 Hz is well above DMX's ~40 frames/s, so a fade is
/// re-evaluated more often than any frame goes out, and audio starts within ~10 ms of being due.
///
/// An exception in one tick is logged (the first one fully, repeats counted) and playback carries on.
/// </summary>
public sealed class PlaybackLoop
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(10);

    readonly Action _tick;
    readonly ILogger _logger;
    readonly ManualResetEventSlim _stop = new();
    readonly FrameTimingRecorder _timing = new();
    Thread? _thread;
    long _errors;
    long _longestTick;   // Stopwatch ticks, since the last TakeLongestTick
    string? _lastError;

    public PlaybackLoop(ShowPlayer player, ILogger logger) : this(player.Tick, logger) { }

    public PlaybackLoop(PlaybackManager manager, ILogger logger) : this(manager.Tick, logger) { }

    PlaybackLoop(Action tick, ILogger logger)
    {
        _tick = tick;
        _logger = logger;
    }

    /// <summary>How regularly ticks actually ran: the scheduling the OS gave the playback thread.</summary>
    public FrameTimingSnapshot Timing => _timing.Snapshot();

    public long ErrorCount => Interlocked.Read(ref _errors);

    public string? LastError => _lastError;

    /// <summary>The longest single update since the last call (diagnostics reads it once a second), then starts over.</summary>
    public TimeSpan TakeLongestTick() => Stopwatch.GetElapsedTime(0, Interlocked.Exchange(ref _longestTick, 0));

    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("The playback loop has already been started.");
        _thread = new Thread(Run) { Name = "playback", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Stops the loop (after the current tick). The player's state is left as it is.</summary>
    public bool Stop(TimeSpan timeout)
    {
        _stop.Set();
        return _thread?.Join(timeout) ?? true;
    }

    void RecordDuration(long elapsed)
    {
        long longest;
        do longest = Interlocked.Read(ref _longestTick);
        while (elapsed > longest && Interlocked.CompareExchange(ref _longestTick, elapsed, longest) != longest);
    }

    void Run()
    {
        long intervalTicks = (long)(Interval.TotalSeconds * Stopwatch.Frequency);
        long next = Stopwatch.GetTimestamp();
        while (!_stop.IsSet)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                _tick();
                _timing.RecordFrame();
            }
            catch (Exception ex)
            {
                // A bug or a misbehaving output. Keep playing; tell the operator once, then count.
                if (Interlocked.Increment(ref _errors) == 1 || ex.Message != _lastError)
                    _logger.LogError(ex, "Playback update failed: {Message}. Playback continues.", ex.Message);
                _lastError = ex.Message;
            }
            RecordDuration(Stopwatch.GetTimestamp() - started);

            // Fixed cadence; if a tick ran long, don't try to catch up with a burst of ticks (position comes from the clock).
            next = Math.Max(next + intervalTicks, Stopwatch.GetTimestamp());
            PreciseDelay.WaitUntil(next, _stop);
        }
    }
}
