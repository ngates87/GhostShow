using GhostShow.Core.Dmx;

namespace GhostShow.Core.Timeline;

public enum TimelineIssueSeverity { Warning, Error }

/// <param name="EventId">The event the problem is about, or null for a timeline-wide problem.</param>
public sealed record TimelineIssue(TimelineIssueSeverity Severity, Guid? EventId, string Message)
{
    public override string ToString() => $"{Severity}: {Message}";
}

/// <summary>One channel's part of a levels event, flattened. Times are TimeSpan ticks.</summary>
readonly record struct LevelStep(long Start, long Fade, int Slot, int Index, byte From, byte To)
{
    public byte ValueAt(long t)
    {
        if (Fade <= 0 || t >= Start + Fade) return To;
        double progress = (double)(t - Start) / Fade;
        return (byte)Math.Round(From + (To - From) * progress);
    }
}

/// <param name="End">When the cue stops; null when it plays until the file ends on its own (length unknown).</param>
/// <param name="Device">The output device to play on (its track's); null for the default output.</param>
public sealed record AudioCue(Guid EventId, string Name, string Path, long Start, long? End, long Offset,
                              double Volume, long FadeIn, long FadeOut, bool Loop, string? Device = null)
{
    /// <summary>Volume at timeline position t from the event volume and its fade-in/out (evaluated from time, not stepped).</summary>
    public double VolumeAt(long t)
    {
        double gain = Volume;
        if (FadeIn > 0 && t < Start + FadeIn) gain *= Math.Clamp((double)(t - Start) / FadeIn, 0, 1);
        if (FadeOut > 0 && End is { } end && t > end - FadeOut) gain *= Math.Clamp((double)(end - t) / FadeOut, 0, 1);
        return gain;
    }

    public bool IsDueAt(long t) => t >= Start && (End is null || t < End);

    /// <summary>Where in the file playback should be at timeline position t (wrapping when looping).</summary>
    public long FileOffsetAt(long t, long? fileLength)
    {
        long played = Math.Max(0, t - Start);
        if (Loop && fileLength is { } length && length - Offset > 0) played %= length - Offset;
        return Offset + played;
    }
}

/// <summary>Levels the timeline wants at one moment: per universe, a value and whether the timeline controls it.</summary>
public sealed class LevelsFrame
{
    internal LevelsFrame(IReadOnlyList<int> universes)
    {
        Universes = universes;
        Values = universes.Select(_ => new byte[DmxUniverse.ChannelCount]).ToArray();
        Controlled = universes.Select(_ => new bool[DmxUniverse.ChannelCount]).ToArray();
    }

    public IReadOnlyList<int> Universes { get; }

    /// <summary>[universe slot][address - 1].</summary>
    public byte[][] Values { get; }

    /// <summary>[universe slot][address - 1]: true where some started event controls the channel.</summary>
    public bool[][] Controlled { get; }

    internal void Clear()
    {
        foreach (var values in Values) Array.Clear(values);
        foreach (var controlled in Controlled) Array.Clear(controlled);
    }
}

/// <summary>
/// A timeline flattened for playback: immutable, sorted, fixture references resolved to DMX addresses, disabled and
/// invalid events dropped. Built by <see cref="TimelineCompiler"/>; swapped into the player as a whole, so playback
/// always sees one consistent version even while the timeline is being edited.
/// </summary>
public sealed class CompiledTimeline
{
    readonly LevelStep[] _steps;   // sorted by Start, then track order, then event and level order

    internal CompiledTimeline(LevelStep[] steps, IReadOnlyList<int> universes, IReadOnlyList<AudioCue> audio,
                              long length, bool loop, IReadOnlyList<TimelineIssue> issues,
                              TimelinePlayback? playback = null)
    {
        _steps = steps;
        Playback = playback ?? new TimelinePlayback();
        Channels = steps.Select(s => (universes[s.Slot], s.Index + 1)).ToHashSet();
        Universes = universes;
        AudioCues = audio;
        Length = length;
        Loop = loop;
        Issues = issues;
    }

    public static CompiledTimeline Empty { get; } = new([], [], [], 0, false, []);

    /// <summary>Universe numbers the timeline drives, in slot order (see <see cref="LevelsFrame"/>).</summary>
    public IReadOnlyList<int> Universes { get; }

    /// <summary>Sorted by start.</summary>
    public IReadOnlyList<AudioCue> AudioCues { get; }

    /// <summary>In ticks.</summary>
    public long Length { get; }

    public bool Loop { get; }

    public IReadOnlyList<TimelineIssue> Issues { get; }

    /// <summary>Priority, overriding and release (from the timeline).</summary>
    public TimelinePlayback Playback { get; }

    /// <summary>Every channel the timeline sets at some point, as (universe, address): what it can override.</summary>
    public IReadOnlySet<(int Universe, int Address)> Channels { get; }

    public int LevelStepCount => _steps.Length;

    public LevelsFrame CreateFrame() => new(Universes);

    /// <summary>
    /// The levels at timeline position t (ticks). Every channel takes the value of the latest-starting event that
    /// controls it: later starts override earlier ones, and on a tie the later track (then the later event in a
    /// track) wins. Fades are evaluated from t, so seeking into the middle of one gives the right value. No allocation.
    /// </summary>
    public void EvaluateLevels(long t, LevelsFrame frame)
    {
        frame.Clear();
        int count = StepsStartedBy(t);
        for (int i = 0; i < count; i++)
        {
            ref readonly var step = ref _steps[i];
            frame.Values[step.Slot][step.Index] = step.ValueAt(t);
            frame.Controlled[step.Slot][step.Index] = true;
        }
    }

    /// <summary>How many steps have started by t: binary search for the first step starting after t.</summary>
    int StepsStartedBy(long t)
    {
        int lo = 0, hi = _steps.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_steps[mid].Start <= t) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
