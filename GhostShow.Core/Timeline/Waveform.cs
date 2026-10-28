namespace GhostShow.Core.Timeline;

/// <summary>
/// An audio file's loudness over time, for drawing it on the timeline: the highest sample (0-1, all channels) in each
/// 1/<see cref="BucketsPerSecond"/> s of the file. Built while the file is decoded to measure its length
/// (<see cref="WaveformBuilder"/>), so it costs no extra decode. One byte per bucket: an hour is about 700 KB.
/// </summary>
public sealed class Waveform
{
    /// <summary>Fine enough to stay smooth at the timeline's closest zoom (2000 px/s: one bucket per 10 px).</summary>
    public const int BucketsPerSecond = 200;

    readonly byte[] _peaks;

    public Waveform(byte[] peaks) => _peaks = peaks;

    public int Count => _peaks.Length;

    public TimeSpan Length => TimeSpan.FromSeconds((double)_peaks.Length / BucketsPerSecond);

    /// <summary>The loudest point (0-1) between two times in the file; 0 outside it.</summary>
    public double PeakBetween(TimeSpan from, TimeSpan to)
    {
        int first = Math.Max(0, (int)(from.TotalSeconds * BucketsPerSecond));
        int last = Math.Min(_peaks.Length - 1, (int)Math.Ceiling(to.TotalSeconds * BucketsPerSecond) - 1);
        if (last < first) last = first;
        if (first >= _peaks.Length) return 0;
        byte peak = 0;
        for (int i = first; i <= last; i++) if (_peaks[i] > peak) peak = _peaks[i];
        return peak / 255.0;
    }
}

/// <summary>Collects a <see cref="Waveform"/> from interleaved float samples as a file is decoded.</summary>
public sealed class WaveformBuilder(int sampleRate, int channels)
{
    readonly List<byte> _peaks = new();
    readonly double _samplesPerBucket = (double)sampleRate * channels / Waveform.BucketsPerSecond;
    long _samples;
    float _peak;

    public void Add(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            float level = Math.Abs(sample);
            if (level > _peak) _peak = level;
            if (++_samples >= (long)Math.Round((_peaks.Count + 1) * _samplesPerBucket)) Flush();
        }
    }

    void Flush()
    {
        _peaks.Add((byte)Math.Round(Math.Min(1, _peak) * 255));
        _peak = 0;
    }

    public Waveform Build()
    {
        if (_samples > Math.Round(_peaks.Count * _samplesPerBucket)) Flush();   // the part-filled last bucket
        return new Waveform(_peaks.ToArray());
    }
}
