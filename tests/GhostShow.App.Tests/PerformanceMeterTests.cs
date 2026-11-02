using GhostShow.App.Controls;
using GhostShow.App.ViewModels;
using GhostShow.Core.Diagnostics;

namespace GhostShow.App.Tests;

public class PerformanceMeterTests
{
    [Fact]
    public void The_load_is_the_worst_part()
    {
        Assert.Equal((0.2, "CPU"), Round(PerformanceMeterViewModel.Score(cpu: 0.2, playbackIntervalP95Ms: 10.5, uiWaitP95Ms: 3)));
        Assert.Equal((0.8, "Playback timing"), Round(PerformanceMeterViewModel.Score(0.2, 30, 3)));   // 20 ms late of 25
        Assert.Equal((1.0, "UI responsiveness"), Round(PerformanceMeterViewModel.Score(0.2, 10, 250)));   // capped at full
        Assert.Equal((0.0, "CPU"), Round(PerformanceMeterViewModel.Score(null, null, 0)));   // nothing measured yet
    }

    [Fact]
    public void A_spike_shows_at_once_and_falls_back_gently()
    {
        double playbackP95 = 10;
        var meter = new PerformanceMeterViewModel(() => new FrameTimingSnapshot(100, 100, 10, playbackP95, 12), () => 0, cpu: () => 0.1);

        playbackP95 = 35;   // 25 ms late: full
        meter.Update();
        Assert.True(meter.Level >= 0.99);
        Assert.Contains("Playback timing is the bottleneck", meter.Tip);

        playbackP95 = 10;
        meter.Update();
        Assert.InRange(meter.Level, 0.3, 0.9);   // on its way down, not dropped
    }

    [Fact]
    public void Segments_go_green_then_yellow_then_red()
    {
        Assert.Equal(LoadMeter.ColorAt(0.1), LoadMeter.ColorAt(0.5));
        Assert.NotEqual(LoadMeter.ColorAt(0.5), LoadMeter.ColorAt(0.7));
        Assert.NotEqual(LoadMeter.ColorAt(0.7), LoadMeter.ColorAt(0.9));
    }

    static (double, string) Round((double Load, string Cause) score) => (Math.Round(score.Load, 6), score.Cause);
}
