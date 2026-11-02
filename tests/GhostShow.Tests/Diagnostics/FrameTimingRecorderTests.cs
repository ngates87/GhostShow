using System.Diagnostics;
using GhostShow.Core.Diagnostics;

namespace GhostShow.Tests.Diagnostics;

public class FrameTimingRecorderTests
{
    static readonly long TicksPerMs = Stopwatch.Frequency / 1000;

    [Fact]
    public void Steady_25ms_frames_report_40_frames_per_second()
    {
        var recorder = new FrameTimingRecorder();
        for (int i = 0; i < 100; i++) recorder.RecordFrame(i * 25 * TicksPerMs);

        var stats = recorder.Snapshot();

        Assert.Equal(100, stats.FramesSent);
        Assert.Equal(40, stats.FramesPerSecond, precision: 1);
        Assert.Equal(25, stats.IntervalP95Ms, precision: 1);
    }

    [Fact]
    public void One_late_frame_shows_up_as_the_maximum_interval()
    {
        var recorder = new FrameTimingRecorder();
        long t = 0;
        for (int i = 0; i < 50; i++) recorder.RecordFrame(t += 25 * TicksPerMs);
        recorder.RecordFrame(t += 200 * TicksPerMs);

        var stats = recorder.Snapshot();

        Assert.Equal(200, stats.IntervalMaxMs, precision: 1);
        Assert.Equal(25, stats.IntervalP50Ms, precision: 1);
    }

    [Fact]
    public void Reset_keeps_an_outage_from_counting_as_one_long_frame()
    {
        var recorder = new FrameTimingRecorder();
        recorder.RecordFrame(0);
        recorder.RecordFrame(25 * TicksPerMs);
        recorder.ResetInterval();
        recorder.RecordFrame(10_000 * TicksPerMs);

        Assert.Equal(25, recorder.Snapshot().IntervalMaxMs, precision: 1);
    }
}
