using System.Diagnostics;
using Avalonia.Threading;

namespace GhostShow.App;

/// <summary>
/// Measures how quickly the UI thread responds: every 100 ms a worker thread posts a no-op at input priority and
/// records how long it waited to run. That wait is the delay a click or keypress would see on top of its own
/// handling. Keeps the last minute of samples.
/// </summary>
public sealed class UiResponsivenessProbe : IDisposable
{
    const int SampleCount = 600;

    readonly Lock _lock = new();
    readonly double[] _samplesMs = new double[SampleCount];
    readonly Timer _timer;
    int _count, _next;

    public UiResponsivenessProbe()
    {
        _timer = new Timer(_ => Probe(), null, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100));
    }

    /// <param name="last">Only the most recent samples (e.g. 20: the last 2 s); all of the last minute by default.</param>
    public (double P50Ms, double P95Ms, double MaxMs, int Samples) Snapshot(int last = SampleCount)
    {
        double[] sorted;
        lock (_lock)
        {
            int n = Math.Min(last, _count);
            sorted = new double[n];
            for (int i = 0; i < n; i++) sorted[i] = _samplesMs[(_next - 1 - i + SampleCount) % SampleCount];
        }
        if (sorted.Length == 0) return (0, 0, 0, 0);
        Array.Sort(sorted);
        double At(double fraction) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(fraction * sorted.Length) - 1)];
        return (At(0.5), At(0.95), sorted[^1], sorted.Length);
    }

    void Probe()
    {
        long posted = Stopwatch.GetTimestamp();
        Dispatcher.UIThread.Post(() =>
        {
            double waitedMs = Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
            lock (_lock)
            {
                _samplesMs[_next] = waitedMs;
                _next = (_next + 1) % SampleCount;
                _count = Math.Min(_count + 1, SampleCount);
            }
        }, DispatcherPriority.Input);
    }

    public void Dispose() => _timer.Dispose();
}
