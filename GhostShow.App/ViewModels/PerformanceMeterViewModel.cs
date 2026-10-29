using CommunityToolkit.Mvvm.ComponentModel;
using GhostShow.Core.Diagnostics;
using GhostShow.Core.Playback;

namespace GhostShow.App.ViewModels;

/// <summary>
/// The status bar's load meter: the worst of the computer's CPU use, how late the playback loop runs, and how long
/// the UI keeps a click waiting, each scaled so the meter turns yellow, then red, as that part starts to hurt the show.
/// Rises at once and falls back gently, like a hardware meter, so a spike is seen.
/// </summary>
public sealed partial class PerformanceMeterViewModel : ObservableObject
{
    /// <summary>Playback updates this much later than their 10 ms interval (p95) fill the meter.</summary>
    const double PlaybackLateFullMs = 25;

    /// <summary>A click waiting this long for the UI thread (p95) fills the meter.</summary>
    const double UiWaitFullMs = 100;

    readonly Func<double?> _cpu;
    readonly Func<FrameTimingSnapshot> _playbackTiming;
    readonly Func<double> _uiWaitP95Ms;

    /// <param name="cpu">CPU use 0-1 since the last call; the whole computer's (<see cref="CpuUsageSampler"/>) by default.</param>
    public PerformanceMeterViewModel(Func<FrameTimingSnapshot> playbackTiming, Func<double> uiWaitP95Ms, Func<double?>? cpu = null)
    {
        _playbackTiming = playbackTiming;
        _uiWaitP95Ms = uiWaitP95Ms;
        _cpu = cpu ?? new CpuUsageSampler().Sample;
        _cpu();   // the first sample only sets the baseline
    }

    /// <summary>0-1, as drawn.</summary>
    [ObservableProperty]
    public partial double Level { get; private set; }

    [ObservableProperty]
    public partial string Tip { get; private set; } = "Measuring…";

    /// <summary>A few times a second, from the UI timer.</summary>
    public void Update()
    {
        double? cpu = _cpu();
        var playback = _playbackTiming();
        double uiWait = _uiWaitP95Ms();
        var (load, cause) = Score(cpu, playback.FramesSent > 0 ? playback.IntervalP95Ms : null, uiWait);

        // Instant attack, gentle release.
        Level = load >= Level ? load : Level - (Level - load) * 0.35;

        string interval = $"{PlaybackLoop.Interval.TotalMilliseconds:0}";
        Tip = $"Load {load:P0}: {(load < Controls.LoadMeter.YellowFrom ? "all good" : $"{cause} is the bottleneck")}\n" +
              $"CPU (whole computer): {(cpu is { } c ? $"{c:P0}" : "…")}\n" +
              $"Playback updates: every {playback.IntervalP95Ms:0.0} ms (p95; target {interval} ms)\n" +
              $"UI response: {uiWait:0} ms (p95, last 2 s)\n" +
              "Click for Diagnostics.";
    }

    /// <summary>The load 0-1 (the worst part) and which part it is.</summary>
    public static (double Load, string Cause) Score(double? cpu, double? playbackIntervalP95Ms, double uiWaitP95Ms)
    {
        double target = PlaybackLoop.Interval.TotalMilliseconds;
        var parts = new[]
        {
            (Load: cpu ?? 0, Cause: "CPU"),
            (Load: playbackIntervalP95Ms is { } p ? (p - target) / PlaybackLateFullMs : 0, Cause: "Playback timing"),
            (Load: uiWaitP95Ms / UiWaitFullMs, Cause: "UI responsiveness"),
        };
        var worst = parts.MaxBy(x => x.Load);
        return (Math.Clamp(worst.Load, 0, 1), worst.Cause);
    }
}
