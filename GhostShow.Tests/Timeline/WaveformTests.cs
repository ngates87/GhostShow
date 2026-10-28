using GhostShow.Core.Timeline;

namespace GhostShow.Tests.Timelines;

public class WaveformTests
{
    /// <summary>Interleaved stereo at 1000 Hz: each second's samples at the given level (alternating sign).</summary>
    static Waveform Build(params double[] secondLevels)
    {
        var builder = new WaveformBuilder(sampleRate: 1000, channels: 2);
        foreach (double level in secondLevels)
        {
            var samples = new float[2000];
            for (int i = 0; i < samples.Length; i++) samples[i] = (float)(i % 2 == 0 ? level : -level);
            builder.Add(samples.AsSpan(0, 700));   // in uneven pieces, as a decoder hands them over
            builder.Add(samples.AsSpan(700));
        }
        return builder.Build();
    }

    [Fact]
    public void Peaks_follow_the_loudness_of_each_part_of_the_file()
    {
        var waveform = Build(0, 0.5, 1);

        Assert.Equal(3 * Waveform.BucketsPerSecond, waveform.Count);
        Assert.Equal(TimeSpan.FromSeconds(3), waveform.Length);
        Assert.Equal(0, waveform.PeakBetween(TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        Assert.Equal(0.5, waveform.PeakBetween(TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(1.3)), 0.01);
        Assert.Equal(1, waveform.PeakBetween(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(2.5)), 0.01);   // the loudest part of a range
        Assert.Equal(0, waveform.PeakBetween(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5)));   // past the end
    }

    [Fact]
    public void A_range_narrower_than_a_bucket_still_reads_that_bucket()
    {
        var waveform = Build(0.25);

        Assert.Equal(0.25, waveform.PeakBetween(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(0.5001)), 0.01);
    }

    [Fact]
    public void A_part_filled_last_bucket_is_kept()
    {
        var builder = new WaveformBuilder(sampleRate: 1000, channels: 1);
        builder.Add(Enumerable.Repeat(0.75f, 1002).ToArray());   // 1.002 s: 200 full buckets and a bit

        var waveform = builder.Build();

        Assert.Equal(201, waveform.Count);
        Assert.Equal(0.75, waveform.PeakBetween(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.005)), 0.01);
    }
}
