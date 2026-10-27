using System.Text.Json.Serialization;
using GhostShow.Core.Fixtures;

namespace GhostShow.Core.Timeline;

/// <summary>
/// One show sequence: tracks of events against time, e.g. a haunt room's loop. A show has any number, organised in
/// folders; each is its own file in the show folder (see <see cref="Storage.ShowFolderStore"/>).
///
/// Time: every time is a <see cref="TimeSpan"/> (integer 100 ns ticks), so comparisons are exact. Event start times
/// are absolute positions on this timeline, measured from 0.
/// </summary>
public sealed class ShowTimeline
{
    /// <summary>Permanent: references to a timeline (clips, events) use it, so renaming or moving the file breaks nothing.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Also its file name (see <see cref="TimelineNames"/> for what's allowed).</summary>
    public string Name { get; set; } = "Main";

    /// <summary>Folder in the show's timeline tree, '/'-separated, "" for the top level. Also its folder on disk.</summary>
    [JsonIgnore]
    public string Folder { get; set; } = "";

    /// <summary>Folder and name, e.g. "Rooms/Troll room": unique in a show, and how the CLI names a timeline.</summary>
    [JsonIgnore]
    public string TreePath => Folder.Length == 0 ? Name : $"{Folder}/{Name}";

    /// <summary>Changed since it was last loaded or saved; only changed timelines are written on save.</summary>
    [JsonIgnore]
    public bool HasUnsavedChanges { get; set; } = true;

    /// <summary>Where it was last loaded from or saved to, relative to the timelines folder; null if never.</summary>
    [JsonIgnore]
    internal string? SavedPath { get; set; }

    /// <summary>Explicit length; null means "until the last event ends".</summary>
    public TimeSpan? Length { get; set; }

    /// <summary>Start again from 0 at the end (a haunt room's scare loop). Otherwise the final look holds at the end.</summary>
    public bool Loop { get; set; }

    /// <summary>Order matters: where two events start at the same moment on the same channel, the lower track wins.</summary>
    public List<TimelineTrack> Tracks { get; set; } = [];

    /// <summary>A deep copy with new IDs throughout (timeline, tracks, events), for Duplicate.</summary>
    public ShowTimeline Clone(string name) => new()
    {
        Name = name, Folder = Folder, Length = Length, Loop = Loop,
        Tracks = Tracks.Select(t => new TimelineTrack
        {
            Name = t.Name, Kind = t.Kind, Enabled = t.Enabled, Color = t.Color, Events = t.Events.Select(e => e.Clone()).ToList(),
            Channels = t.Channels.Select(c => c.Clone()).ToList(),
        }).ToList(),
    };
}

/// <summary>
/// Timeline and folder names are file and folder names on disk, so they follow the strictest rules of the supported
/// systems (Windows): no \ / : * ? " &lt; &gt; |, no control characters, no trailing space or dot, not a reserved
/// device name. Names are unique within a folder, ignoring case (two names differing only in case would clash on
/// Windows and macOS).
/// </summary>
public static class TimelineNames
{
    static readonly char[] Invalid = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>Why a name can't be used, or null if it can.</summary>
    public static string? Problem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A name can't be empty.";
        if (name.IndexOfAny(Invalid) >= 0 || name.Any(char.IsControl)) return "A name can't contain \\ / : * ? \" < > | (it's also a file name).";
        if (name.EndsWith(' ') || name.EndsWith('.') || name.StartsWith(' ')) return "A name can't start with a space or end with a space or a dot.";
        if (name.StartsWith('.')) return "A name can't start with a dot (that would be a hidden file).";
        if (Reserved.Contains(name.Split('.')[0])) return $"'{name}' is reserved on Windows.";
        if (name.Length > 120) return "A name can be at most 120 characters.";
        return null;
    }

    /// <summary>Makes any text usable as a name: invalid characters become '-'; falls back to the given default.</summary>
    public static string Sanitize(string text, string fallback)
    {
        string name = new string(text.Select(c => Array.IndexOf(Invalid, c) >= 0 || char.IsControl(c) ? '-' : c).ToArray()).Trim(' ', '.');
        return Problem(name) is null ? name : fallback;
    }

    /// <summary>The first of "base", "base 2", "base 3", ... not in use.</summary>
    public static string Unique(string baseName, IEnumerable<string> existing)
    {
        var taken = existing.ToHashSet(Comparer);
        if (!taken.Contains(baseName)) return baseName;
        for (int i = 2; ; i++)
            if (!taken.Contains($"{baseName} {i}")) return $"{baseName} {i}";
    }

    /// <summary>The parent of a '/'-separated folder path ("" for a top-level folder).</summary>
    public static string ParentOf(string folder) => folder.LastIndexOf('/') is var i and >= 0 ? folder[..i] : "";

    /// <summary>The last segment of a folder path.</summary>
    public static string LeafOf(string folder) => folder[(folder.LastIndexOf('/') + 1)..];

    public static string Combine(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";

    /// <summary>Whether path is folder itself or inside it.</summary>
    public static bool IsWithin(string path, string folder) =>
        Comparer.Equals(path, folder) || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
}

[JsonConverter(typeof(JsonStringEnumConverter<TrackKind>))]
public enum TrackKind { Lighting, Audio, Markers }

public sealed class TimelineTrack
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Track";
    public TrackKind Kind { get; set; }

    /// <summary>A disabled (muted) track produces no output at all, as if its events weren't there.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Display colour, e.g. "#2E7D9A"; null for the kind's default.</summary>
    public string? Color { get; set; }

    /// <summary>The track's blocks: audio events, markers, or (lighting) <see cref="LightingClip"/>s.</summary>
    public List<TimelineEvent> Events { get; set; } = [];

    /// <summary>
    /// Lighting tracks: the channels this track controls (what, and its colour). Their levels are in the track's
    /// <see cref="LightingClip"/>s. Points directly on these lanes (shows saved before lighting had blocks) are moved
    /// into a block when the show is opened (<see cref="LevelEventConversion.Upgrade"/>), but still play if present.
    /// </summary>
    public List<ChannelLane> Channels { get; set; } = [];
}

/// <summary>
/// What a lighting lane controls. A fixture's logical control (Intensity, Red, ...) or one of its channels by number
/// (1 = its first), so the lane follows the fixture when it's re-addressed; or, for anything unpatched, a raw DMX
/// address on a universe.
/// </summary>
public sealed record LaneTarget(Guid? FixtureId = null, ChannelFunction? Control = null, int? Channel = null, int Universe = Model.Project.FirstUniverse)
{
    public static LaneTarget FixtureControl(Guid fixtureId, ChannelFunction control) => new(fixtureId, Control: control);
    public static LaneTarget FixtureChannel(Guid fixtureId, int channel) => new(fixtureId, Channel: channel);
    public static LaneTarget Raw(int universe, int address) => new(Universe: universe, Channel: address);

    public bool IsRaw => FixtureId is null;
}

/// <summary>
/// One controlled channel on a lighting track: its level over time as points. How it plays:
/// - before the first point the timeline doesn't touch the channel (manual control shows);
/// - from each point to the next the level fades linearly, or, if the next point is a step, holds and then jumps;
/// - after the last point the level holds.
/// Points are kept in time order; at equal times the later one in the list wins.
/// </summary>
public sealed class ChannelLane
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public LaneTarget Target { get; set; } = new();

    /// <summary>Line colour, e.g. "#FF5252"; null for a colour chosen from the target (red for Red, ...).</summary>
    public string? Color { get; set; }

    public List<CurvePoint> Points { get; set; } = [];

    public ChannelLane Clone() => new() { Target = Target, Color = Color, Points = Points.Select(p => p.Clone()).ToList() };

    /// <summary>The level at a time as it would play, or null before the first point.</summary>
    public byte? ValueAt(TimeSpan time)
    {
        CurvePoint? previous = null;
        foreach (var point in Points)
        {
            if (point.Time > time)
            {
                if (previous is null) return null;
                if (point.Step) return previous.Value;
                double f = (time - previous.Time).Ticks / (double)(point.Time - previous.Time).Ticks;
                return (byte)Math.Round(previous.Value + (point.Value - previous.Value) * f);
            }
            previous = point;
        }
        return previous?.Value;
    }

    /// <summary>Puts points back in time order (stable, so equal times keep their order).</summary>
    public void Sort() => Points = Points.OrderBy(p => p.Time).ToList();
}

/// <param name="Step">How the level gets here from the previous point: false fades linearly, true holds the previous
/// level and jumps at this point's time.</param>
public sealed class CurvePoint
{
    public TimeSpan Time { get; set; }

    /// <summary>DMX level, 0-255.</summary>
    public byte Value { get; set; }

    /// <summary>Jump to this level at <see cref="Time"/> instead of fading to it from the previous point.</summary>
    public bool Step { get; set; }

    public CurvePoint Clone() => new() { Time = Time, Value = Value, Step = Step };
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LightingClip), "lighting")]
[JsonDerivedType(typeof(LevelsEvent), "levels")]
[JsonDerivedType(typeof(AudioEvent), "audio")]
[JsonDerivedType(typeof(MarkerEvent), "marker")]
public abstract class TimelineEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Optional label shown on the event block.</summary>
    public string? Name { get; set; }

    /// <summary>Absolute position on the timeline. Must not be negative.</summary>
    public TimeSpan Start { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>A deep copy with a new ID, for copy/paste and duplicate.</summary>
    public abstract TimelineEvent Clone();
}

/// <summary>
/// A block on a lighting track ("Level Control"): a stretch of time holding the level lines of the track's channels
/// during it, as points relative to the block's start. Moving the block moves its lines; resizing it stretches them.
/// After the block ends, its channels hold their last level until something else changes them.
/// </summary>
public sealed class LightingClip : TimelineEvent
{
    public static readonly TimeSpan DefaultLength = TimeSpan.FromSeconds(4);

    public TimeSpan Length { get; set; } = DefaultLength;

    /// <summary>One per channel it has levels for (a subset of the track's channels); point times from 0 to Length.</summary>
    public List<ChannelLane> Lanes { get; set; } = [];

    public ChannelLane? LaneFor(LaneTarget target) => Lanes.FirstOrDefault(l => l.Target == target);

    /// <summary>Changes the length, scaling every point's time with it (a 4 s flicker made 8 s plays at half speed).</summary>
    public void Stretch(TimeSpan length)
    {
        if (length <= TimeSpan.Zero) return;
        double scale = Length > TimeSpan.Zero ? (double)length.Ticks / Length.Ticks : 1;
        foreach (var point in Lanes.SelectMany(l => l.Points)) point.Time = TimeSpan.FromTicks((long)Math.Round(point.Time.Ticks * scale));
        Length = length;
    }

    public override TimelineEvent Clone() => new LightingClip
    {
        Name = Name, Start = Start, Enabled = Enabled, Length = Length, Lanes = Lanes.Select(l => l.Clone()).ToList(),
    };
}

/// <summary>
/// Sets lighting levels, either snapping (Fade = 0) or fading linearly over Fade, then holding until a later event
/// changes the same channel. Targets either raw DMX channels on a universe, or a patched fixture.
///
/// The form lighting took before tracks had channels (<see cref="TimelineTrack.Channels"/>). Shows that still have
/// them are converted to points when opened (<see cref="LevelEventConversion"/>); the editor doesn't make them. The
/// compiler still plays them, as cue-style input for code and tests.
/// </summary>
public sealed class LevelsEvent : TimelineEvent
{
    /// <summary>The fixture to control; null for raw channels on <see cref="Universe"/>.</summary>
    public Guid? FixtureId { get; set; }

    /// <summary>Universe of raw channel levels. Ignored for fixture events (the fixture's patch decides).</summary>
    public int Universe { get; set; } = Model.Project.FirstUniverse;

    public List<Level> Levels { get; set; } = [];

    /// <summary>How long the change takes. Zero snaps to the new levels at Start.</summary>
    public TimeSpan Fade { get; set; }

    public override TimelineEvent Clone() => new LevelsEvent
    {
        Name = Name, Start = Start, Enabled = Enabled, FixtureId = FixtureId, Universe = Universe, Fade = Fade,
        Levels = Levels.Select(l => l with { }).ToList(),
    };
}

/// <summary>
/// One target value. For raw channels, Channel is the DMX address (1-512). For a fixture, either Control (a logical
/// control such as Intensity or Red) or Channel (1-based within the fixture's mode).
/// </summary>
/// <param name="From">Where a fade starts. Null: from whatever the timeline had on that channel when the fade starts (0 if nothing).</param>
public sealed record Level(byte To, int? Channel = null, ChannelFunction? Control = null, byte? From = null);

/// <summary>Plays (part of) an audio file. The file is referenced, never embedded.</summary>
public sealed class AudioEvent : TimelineEvent
{
    /// <summary>Relative to the show file's folder, or absolute (see docs/timeline.md, "Audio files").</summary>
    public string File { get; set; } = "";

    /// <summary>How long to play; null plays to the end of the file (or, when looping, until the timeline ends).</summary>
    public TimeSpan? Length { get; set; }

    /// <summary>Where in the file to start.</summary>
    public TimeSpan Offset { get; set; }

    /// <summary>0-1.</summary>
    public double Volume { get; set; } = 1.0;

    public TimeSpan FadeIn { get; set; }
    public TimeSpan FadeOut { get; set; }

    /// <summary>Repeat the file (from Offset) until Length runs out.</summary>
    public bool Loop { get; set; }

    public override TimelineEvent Clone() => new AudioEvent
    {
        Name = Name, Start = Start, Enabled = Enabled, File = File, Length = Length, Offset = Offset,
        Volume = Volume, FadeIn = FadeIn, FadeOut = FadeOut, Loop = Loop,
    };
}

/// <summary>A named point in the show ("Scare 1"). Produces no output; reported when playback crosses it.</summary>
public sealed class MarkerEvent : TimelineEvent
{
    public override TimelineEvent Clone() => new MarkerEvent { Name = Name, Start = Start, Enabled = Enabled };
}
