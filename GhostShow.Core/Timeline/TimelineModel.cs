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

    /// <summary>How it plays alongside other timelines (priority, overriding, release). See <see cref="TimelinePlayback"/>.</summary>
    public TimelinePlayback Playback { get; set; } = new();

    /// <summary>A deep copy with new IDs throughout (timeline, tracks, events), for Duplicate.</summary>
    public ShowTimeline Clone(string name) => new()
    {
        Name = name, Folder = Folder, Length = Length, Loop = Loop, Playback = Playback,
        Tracks = Tracks.Select(t => new TimelineTrack
        {
            Name = t.Name, Kind = t.Kind, Enabled = t.Enabled, Color = t.Color, DrawLevelsBetweenClips = t.DrawLevelsBetweenClips, Volume = t.Volume,
            Events = t.Events.Select(e => e.Clone()).ToList(),
            Channels = t.Channels.Select(c => c.Clone()).ToList(),
        }).ToList(),
    };
}

/// <summary>
/// How a timeline plays alongside others. Several timelines can play at once (a room's ambient loop, a scare
/// triggered on top). On a channel two of them set, the higher <see cref="Priority"/> wins; on equal priority, the one
/// started most recently. A timeline with a higher priority than another is said to override it:
/// </summary>
/// <param name="Priority">Higher wins channels. 0 for ordinary timelines; e.g. 10 for scares, 100 for an emergency look.</param>
/// <param name="PauseOverridden">While this plays, lower-priority timelines that share a channel with it pause (and
/// resume where they were when it ends). Otherwise they keep running underneath, out of sight, and are where they've
/// got to when it ends.</param>
/// <param name="ReleaseFade">When this stops or (with <paramref name="ReleaseAtEnd"/>) ends, its channels fade back to
/// whatever is underneath (another timeline, or the manual level) over this time. Zero: snap back.</param>
/// <param name="DuckOthers">While this plays, lower-priority timelines' sounds play at this volume (0-1). 1: untouched.</param>
/// <param name="ReleaseAtEnd">At the end (when not looping), let go of its channels instead of holding its final look:
/// what an override usually wants, so the ambient comes back by itself.</param>
public sealed record TimelinePlayback(int Priority = 0, bool PauseOverridden = false, TimeSpan ReleaseFade = default,
                                      double DuckOthers = 1, bool ReleaseAtEnd = false);

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
public enum TrackKind { Lighting, Audio }

public sealed class TimelineTrack
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Track";
    public TrackKind Kind { get; set; }

    /// <summary>A disabled (muted) track produces no output at all, as if its events weren't there.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Display colour, e.g. "#2E7D9A"; null for the kind's default.</summary>
    public string? Color { get; set; }

    /// <summary>
    /// Audio tracks: 0-1, multiplied into every clip's own volume, so whole tracks can be balanced against each other
    /// (ambience under a voice-over) without touching their clips.
    /// </summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>The track's clips: audio events, or (lighting) <see cref="LightingClip"/>s.</summary>
    public List<TimelineEvent> Events { get; set; } = [];

    /// <summary>
    /// Lighting tracks: the channels this track controls (what, its colour, and its level before the first clip).
    /// Their levels are in the track's <see cref="LightingClip"/>s (see <see cref="TrackLevels"/>). Points directly on
    /// these lanes (shows saved before lighting had clips) are moved into a clip when the show is opened
    /// (<see cref="LevelEventConversion.Upgrade"/>), but still play if present.
    /// </summary>
    public List<ChannelLane> Channels { get; set; } = [];

    /// <summary>Lighting, expanded: draw each channel's held level between clips as a line (VenueMagic's "Draw lamp levels between clips").</summary>
    public bool DrawLevelsBetweenClips { get; set; } = true;
}

/// <summary>Where a lighting track's channel is before its first clip, and so where that clip's envelope starts.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChannelStart>))]
public enum ChannelStart
{
    /// <summary>The timeline leaves the channel alone until the first clip, whose envelope starts at <see cref="ChannelLane.StartLevel"/>.</summary>
    FirstClip,

    /// <summary>The timeline holds the channel at <see cref="ChannelLane.StartLevel"/> from its start.</summary>
    Fixed,
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
/// One channel's levels over time as points: a channel of a lighting track (<see cref="TimelineTrack.Channels"/>), or
/// its envelope in a clip (<see cref="LightingClip.Lanes"/>, see there for the envelope rules). Points directly on a
/// track channel (from before clips) play like this:
/// - before the first point the timeline doesn't touch the channel (manual control shows);
/// - from each point to the next the level follows the next point's <see cref="CurvePoint.Curve"/>;
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

    /// <summary>Track channels: where the channel is before the first clip (see <see cref="ChannelStart"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ChannelStart Start { get; set; }

    /// <summary>Track channels: the level the first clip starts from (and, with <see cref="ChannelStart.Fixed"/>, held before it).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public byte StartLevel { get; set; }

    public ChannelLane Clone() => new()
    {
        Target = Target, Color = Color, Start = Start, StartLevel = StartLevel, Points = Points.Select(p => p.Clone()).ToList(),
    };

    /// <summary>The level at a time as it would play, or null before the first point.</summary>
    public byte? ValueAt(TimeSpan time)
    {
        CurvePoint? previous = null;
        foreach (var point in Points)
        {
            if (point.Time > time) return previous is null ? null : Envelope.Round(Envelope.ValueAt(Points, time));
            previous = point;
        }
        return previous?.Value;
    }

    /// <summary>Puts points back in time order (stable, so equal times keep their order).</summary>
    public void Sort() => Points = Points.OrderBy(p => p.Time).ToList();
}

/// <summary>How the level gets from one point (envelope handle) to the next: the curve type of the later point.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CurveKind>))]
public enum CurveKind
{
    /// <summary>A straight fade.</summary>
    Linear,

    /// <summary>Hold the previous level, then jump at this point's time.</summary>
    Step,

    /// <summary>A smooth curve through the points (monotone: it never overshoots them).</summary>
    Spline,
}

/// <summary>A point on a channel's line: an envelope handle, in VenueMagic's terms.</summary>
public sealed class CurvePoint
{
    public TimeSpan Time { get; set; }

    /// <summary>DMX level, 0-255.</summary>
    public byte Value { get; set; }

    /// <summary>How the level gets here from the previous point.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CurveKind Curve { get; set; }

    /// <summary>Files before timeline version 4 had "step": true instead of a curve type.</summary>
    [JsonInclude, JsonPropertyName("step"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    internal bool LegacyStep
    {
        get => false;
        set { if (value) Curve = CurveKind.Step; }
    }

    public CurvePoint Clone() => new() { Time = Time, Value = Value, Curve = Curve };
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LightingClip), "lighting")]
[JsonDerivedType(typeof(LevelsEvent), "levels")]
[JsonDerivedType(typeof(AudioEvent), "audio")]
public abstract class TimelineEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Optional label shown on the event block.</summary>
    public string? Name { get; set; }

    /// <summary>Absolute position on the timeline. Must not be negative.</summary>
    public TimeSpan Start { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Locked: can't be moved, resized, split or edited (VenueMagic's "Lock clip"), until unlocked.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Locked { get; set; }

    /// <summary>Clips sharing a link group move together. Null: not linked.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? LinkGroup { get; set; }

    /// <summary>A deep copy with a new ID, for copy/paste and duplicate (not locked or linked).</summary>
    public abstract TimelineEvent Clone();
}

/// <summary>
/// A clip on a lighting track: VenueMagic's "Level Control" effect. It holds an envelope per channel of the track
/// (<see cref="Lanes"/>; a channel without one holds its level through the clip), as handles timed from the clip's
/// start:
/// - every envelope starts with a start handle at 0, which isn't stored: its level is where the previous clip left
///   the channel (or the channel's <see cref="ChannelLane.StartLevel"/> for the first clip), so clips carry on from
///   each other;
/// - every envelope ends with an end handle at <see cref="Length"/> (stored; its level can change, its time can't);
/// - handles in between can't pass their neighbours.
/// After the clip, the channel holds its end level until the next clip. A clip that starts while another is still
/// running takes over from its start. See <see cref="TrackLevels"/>.
/// With an <see cref="Effect"/> it's a built-in effect clip instead (Lightning, Fire, …): its envelopes are made from
/// the effect's settings, for every channel of the track, and its <see cref="Lanes"/> aren't used.
/// </summary>
public sealed class LightingClip : TimelineEvent
{
    public static readonly TimeSpan DefaultLength = TimeSpan.FromSeconds(4);
    public const string DefaultName = "Level Control";

    public TimeSpan Length { get; set; } = DefaultLength;

    /// <summary>One per channel it has an envelope for (a subset of the track's channels); handle times from 0 to Length.</summary>
    public List<ChannelLane> Lanes { get; set; } = [];

    /// <summary>Resizing scales the envelopes with the clip (on), or crops them, keeping their handles' times (off).</summary>
    public bool ScaleOnResize { get; set; } = true;

    /// <summary>A built-in effect this clip plays instead of its own envelopes; null for a Level Control clip.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LightingEffect? Effect { get; set; }

    /// <summary>What the clip is: "Level Control", or the effect's name.</summary>
    [JsonIgnore]
    public string KindName => Effect is { } effect ? EffectInfo.Of(effect.Kind).Name : DefaultName;

    public ChannelLane? LaneFor(LaneTarget target) => Lanes.FirstOrDefault(l => l.Target == target);

    /// <summary>A channel's envelope with its start handle: from startLevel at 0, through the handles, to the end.</summary>
    public IReadOnlyList<CurvePoint> EnvelopeFor(LaneTarget target, byte startLevel) =>
        Envelope.WithStart(LaneFor(target)?.Points, startLevel, Length);

    /// <summary>Changes the length, scaling every handle's time with it (a 4 s flicker made 8 s plays at half speed).</summary>
    public void Stretch(TimeSpan length)
    {
        if (length <= TimeSpan.Zero) return;
        double scale = Length > TimeSpan.Zero ? (double)length.Ticks / Length.Ticks : 1;
        foreach (var point in Lanes.SelectMany(l => l.Points)) point.Time = TimeSpan.FromTicks((long)Math.Round(point.Time.Ticks * scale));
        Length = length;
        EnsureEndHandles();
    }

    /// <summary>
    /// Moves the end to a new length: scaling the envelopes (<see cref="ScaleOnResize"/>), or cropping them: made
    /// shorter, handles past the new end go and the end handle takes the level there; made longer, the old end
    /// handle stays where it was and a new one holds its level to the new end.
    /// </summary>
    /// <param name="startLevels">Each channel's start handle level (its level as the clip starts), for levels part-way along.</param>
    public void ResizeEnd(TimeSpan length, Func<LaneTarget, byte> startLevels)
    {
        if (length <= TimeSpan.Zero || length == Length) return;
        if (ScaleOnResize)
        {
            Stretch(length);
            return;
        }
        foreach (var lane in Lanes)
        {
            if (length < Length)
            {
                var envelope = Envelope.WithStart(lane.Points, startLevels(lane.Target), Length);
                byte level = Envelope.Round(Envelope.ValueAt(envelope, length));
                var cut = lane.Points.FirstOrDefault(p => p.Time >= length);
                lane.Points.RemoveAll(p => p.Time >= length);
                lane.Points.Add(new CurvePoint { Time = length, Value = level, Curve = cut?.Curve == CurveKind.Step ? CurveKind.Linear : cut?.Curve ?? CurveKind.Linear });
            }
            else if (lane.Points.Count > 0)
            {
                lane.Points.Add(new CurvePoint { Time = length, Value = lane.Points[^1].Value });
            }
        }
        Length = length;
    }

    /// <summary>
    /// Moves the start to a new time, keeping the end where it is: scaling the envelopes, or cropping them (handles
    /// keep their place on the timeline; those before the new start go).
    /// </summary>
    public void ResizeStart(TimeSpan start)
    {
        var end = Start + Length;
        if (start >= end || start == Start) return;
        if (ScaleOnResize)
        {
            Stretch(end - start);
            Start = start;
            return;
        }
        var shift = Start - start;   // handles move this much later in the clip
        Start = start;
        Length = end - start;
        foreach (var lane in Lanes.Where(l => l.Points.Count > 0))
        {
            var endHandle = lane.Points[^1];
            foreach (var point in lane.Points) point.Time += shift;
            lane.Points.RemoveAll(p => p.Time <= TimeSpan.Zero && p != endHandle);
            endHandle.Time = Length;
        }
    }

    /// <summary>
    /// Splits the clip at a time inside it (relative to its start): this clip keeps the part before, ending there at
    /// the levels it had reached, and the returned clip (a new one, starting there) holds the rest. It starts where
    /// this one ends, so the two play as the one did.
    /// </summary>
    public LightingClip? SplitAt(TimeSpan at, Func<LaneTarget, byte> startLevels)
    {
        if (at <= TimeSpan.Zero || at >= Length || Effect is not null) return null;   // an effect is made whole: convert it to split it
        var after = new LightingClip { Name = Name, Enabled = Enabled, Start = Start + at, Length = Length - at, ScaleOnResize = ScaleOnResize };
        foreach (var lane in Lanes)
        {
            var envelope = Envelope.WithStart(lane.Points, startLevels(lane.Target), Length);
            byte level = Envelope.Round(Envelope.ValueAt(envelope, at));
            var later = lane.Points.Where(p => p.Time > at).ToList();
            after.Lanes.Add(new ChannelLane
            {
                Target = lane.Target, Color = lane.Color,
                Points = later.Select(p => new CurvePoint { Time = p.Time - at, Value = p.Value, Curve = p.Curve }).ToList(),
            });
            var cut = later.FirstOrDefault();
            lane.Points.RemoveAll(p => p.Time >= at);
            lane.Points.Add(new CurvePoint { Time = at, Value = level, Curve = cut?.Curve == CurveKind.Step ? CurveKind.Linear : cut?.Curve ?? CurveKind.Linear });
        }
        Length = at;
        return after;
    }

    /// <summary>
    /// Puts every envelope in order and gives it its end handle: handles past the end go, and an envelope without a
    /// handle at <see cref="Length"/> gets one holding its last level. Empty envelopes are left empty (the channel
    /// holds its level through the clip).
    /// </summary>
    public void EnsureEndHandles()
    {
        foreach (var lane in Lanes)
        {
            lane.Sort();
            lane.Points.RemoveAll(p => p.Time > Length || p.Time < TimeSpan.Zero);
            if (lane.Points.Count > 0 && lane.Points[^1].Time != Length)
                lane.Points.Add(new CurvePoint { Time = Length, Value = lane.Points[^1].Value });
        }
    }

    /// <summary>Whether a point is the end handle of one of this clip's envelopes (its time is fixed; it can't be deleted).</summary>
    public bool IsEndHandle(CurvePoint point) => Lanes.Any(l => l.Points.Count > 0 && l.Points[^1] == point);

    public override TimelineEvent Clone() => new LightingClip
    {
        Name = Name, Start = Start, Enabled = Enabled, Length = Length, ScaleOnResize = ScaleOnResize, Lanes = Lanes.Select(l => l.Clone()).ToList(),
        Effect = Effect?.Clone(),
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
