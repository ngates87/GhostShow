using GhostShow.Core.Diagnostics;
using GhostShow.Core.Playback;

namespace GhostShow.Server;

/// <summary>
/// Measures how hard the show computer is working (<see cref="LoadReading"/>): the worst of the whole computer's CPU
/// use, how late the playback loop runs, and how long the show's thread keeps work waiting, each scaled so the meter
/// turns yellow, then red, as that part starts to hurt the show.
/// </summary>
public sealed class ShowLoad
{
    /// <summary>Playback updates this much later than their 10 ms interval (p95) fill the meter.</summary>
    const double PlaybackLateFullMs = 25;

    /// <summary>Work waiting this long for the show's thread (p95) fills the meter.</summary>
    const double WaitFullMs = 100;

    readonly Func<double?> _cpu;
    readonly Func<FrameTimingSnapshot> _playbackTiming;
    readonly Func<double> _waitP95Ms;
    volatile LoadReading _reading = LoadReading.Measuring;

    /// <param name="waitP95Ms">How long work waits for the show's thread (p95, recent), from its <see cref="ShowThreadProbe"/>.</param>
    /// <param name="cpu">CPU use 0-1 since the last call; the whole computer's (<see cref="CpuUsageSampler"/>) by default.</param>
    public ShowLoad(Func<FrameTimingSnapshot> playbackTiming, Func<double> waitP95Ms, Func<double?>? cpu = null)
    {
        _playbackTiming = playbackTiming;
        _waitP95Ms = waitP95Ms;
        _cpu = cpu ?? new CpuUsageSampler().Sample;
        _cpu();   // the first sample only sets the baseline
    }

    public ShowLoad(ShowServices services) : this(() => services.PlaybackLoop.Timing, () => services.Probe.Snapshot(last: 20).P95Ms)
    {
    }

    /// <summary>The last measurement (any thread).</summary>
    public LoadReading Reading => _reading;

    /// <summary>Measures now (a couple of times a second): CPU use is since the last call.</summary>
    public LoadReading Measure()
    {
        double? cpu = _cpu();
        var playback = _playbackTiming();
        double wait = _waitP95Ms();
        var (load, cause) = Score(cpu, playback.FramesSent > 0 ? playback.IntervalP95Ms : null, wait);
        string interval = $"{PlaybackLoop.Interval.TotalMilliseconds:0}";
        string tip = $"Show computer's load {load:P0}: {(load < LoadReading.YellowFrom ? "all good" : $"{cause} is the bottleneck")}\n" +
                     $"CPU (the whole show computer): {(cpu is { } c ? $"{c:P0}" : "…")}\n" +
                     $"Playback updates: every {playback.IntervalP95Ms:0.0} ms (p95; target {interval} ms)\n" +
                     $"Show response: {wait:0} ms (p95, last 2 s)\n" +
                     "Click for Diagnostics.";
        return _reading = new LoadReading(load, tip);
    }

    /// <summary>The load 0-1 (the worst part) and which part it is.</summary>
    public static (double Load, string Cause) Score(double? cpu, double? playbackIntervalP95Ms, double waitP95Ms)
    {
        double target = PlaybackLoop.Interval.TotalMilliseconds;
        var parts = new[]
        {
            (Load: cpu ?? 0, Cause: "CPU"),
            (Load: playbackIntervalP95Ms is { } p ? (p - target) / PlaybackLateFullMs : 0, Cause: "Playback timing"),
            (Load: waitP95Ms / WaitFullMs, Cause: "Show responsiveness"),
        };
        var worst = parts.MaxBy(x => x.Load);
        return (Math.Clamp(worst.Load, 0, 1), worst.Cause);
    }
}
