using System.Diagnostics;

namespace GhostShow.Core.Diagnostics;

public readonly record struct FrameTimingSnapshot(
    long FramesSent,
    double FramesPerSecond,
    double IntervalP50Ms,
    double IntervalP95Ms,
    double IntervalMaxMs);

/// <summary>
/// Measures how regularly frames go out. The output thread calls <see cref="RecordFrame"/> (no allocation);
/// the UI or a test calls <see cref="Snapshot"/> whenever it wants numbers. Statistics cover the last
/// <see cref="WindowSize"/> frames, about 6 s at DMX rates, so a stall shows up quickly and then ages out.
/// </summary>
public sealed class FrameTimingRecorder
{
    public const int WindowSize = 256;

    readonly Lock _lock = new();
    readonly long[] _intervalTicks = new long[WindowSize];
    int _intervalCount, _nextInterval;
    long _lastFrameTimestamp = -1;
    long _framesSent;

    public void RecordFrame() => RecordFrame(Stopwatch.GetTimestamp());

    /// <summary>For tests: record a frame sent at a given Stopwatch timestamp.</summary>
    public void RecordFrame(long timestamp)
    {
        lock (_lock)
        {
            _framesSent++;
            if (_lastFrameTimestamp >= 0)
            {
                _intervalTicks[_nextInterval] = timestamp - _lastFrameTimestamp;
                _nextInterval = (_nextInterval + 1) % WindowSize;
                _intervalCount = Math.Min(_intervalCount + 1, WindowSize);
            }
            _lastFrameTimestamp = timestamp;
        }
    }

    /// <summary>Forgets interval history, e.g. after a reconnect, so the outage doesn't count as one slow frame.</summary>
    public void ResetInterval()
    {
        lock (_lock) _lastFrameTimestamp = -1;
    }

    public FrameTimingSnapshot Snapshot()
    {
        long[] sorted;
        long framesSent;
        lock (_lock)
        {
            framesSent = _framesSent;
            sorted = _intervalTicks.AsSpan(0, _intervalCount).ToArray();
        }
        if (sorted.Length == 0) return new FrameTimingSnapshot(framesSent, 0, 0, 0, 0);

        Array.Sort(sorted);
        double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        double meanMs = ToMs(sorted.Sum()) / sorted.Length;
        return new FrameTimingSnapshot(
            framesSent,
            FramesPerSecond: meanMs > 0 ? 1000.0 / meanMs : 0,
            IntervalP50Ms: ToMs(Percentile(sorted, 0.50)),
            IntervalP95Ms: ToMs(Percentile(sorted, 0.95)),
            IntervalMaxMs: ToMs(sorted[^1]));
    }

    static long Percentile(long[] sorted, double fraction) =>
        sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(fraction * sorted.Length) - 1)];
}
