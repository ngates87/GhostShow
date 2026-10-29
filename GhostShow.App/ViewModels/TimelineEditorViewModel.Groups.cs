using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Timeline;

namespace GhostShow.App.ViewModels;

/// <summary>What a group of handles is: a fixture's colour, its position, or a look across channels (a snapshot).</summary>
public enum HandleGroupKind { Colour, Position, Look }

/// <summary>
/// Grouped handles (VenueMagic's RGB and position handles, and Snapshot): handles on several channels at one time,
/// made together. They share <see cref="CurvePoint.Group"/> within their clip, so selecting one selects them all,
/// and the usual moves, nudges and deletes then treat them as one. A colour group is set with a colour, a position
/// group with pan and tilt.
/// </summary>
public sealed partial class TimelineEditorViewModel
{
    /// <summary>The selected handles, when they're exactly one whole group; null otherwise.</summary>
    [ObservableProperty]
    public partial HandleGroupViewModel? SelectedHandleGroup { get; private set; }

    /// <summary>What a channel does: its control, or (by number) from the patch. Null for raw channels and missing fixtures.</summary>
    public ChannelFunction? FunctionOf(LaneTarget target) =>
        TimelineCompiler.FunctionOf(target, _session.Patch.Fixtures.ToDictionary(f => f.Id));

    /// <summary>A handle and the others in its group (in its clip), or just itself.</summary>
    public IReadOnlyList<CurvePoint> GroupOf(CurvePoint point)
    {
        if (point.Group is not { } group || ClipOf(point) is not { } clip) return [point];
        return clip.Lanes.SelectMany(l => l.Points).Where(p => p.Group == group).ToList();
    }

    IEnumerable<CurvePoint> WithGroups(IEnumerable<CurvePoint> points) => points.SelectMany(GroupOf).Distinct();

    /// <summary>The channels of a fixture on a track that a colour (red, green, blue, white, amber) or position (pan, tilt) handle sets.</summary>
    public IReadOnlyList<LaneTarget> GroupTargets(TimelineTrack track, Guid fixtureId, HandleGroupKind kind) =>
        track.Channels.Where(c => c.Target.FixtureId == fixtureId && FunctionOf(c.Target) is { } f && kind switch
            {
                HandleGroupKind.Colour => LightingEffects.IsColour(f) && f != ChannelFunction.UV,
                HandleGroupKind.Position => f is ChannelFunction.Pan or ChannelFunction.Tilt,
                _ => false,
            })
            .Select(c => c.Target).ToList();

    /// <summary>A colour or position handle: grouped handles on the fixture's colour (or pan and tilt) channels at a time, at the levels they have there. One undo step.</summary>
    public IReadOnlyList<CurvePoint> AddGroupHandle(LightingClip clip, Guid fixtureId, TimeSpan time, HandleGroupKind kind)
    {
        if (clip.Locked || clip.Effect is not null || TrackOf(clip) is not { } track) return [];
        var at = SnapTime(time) - clip.Start;
        if (at <= TimeSpan.Zero || at >= clip.Length) at = time - clip.Start;
        if (at <= TimeSpan.Zero || at >= clip.Length) return [];
        var targets = GroupTargets(track, fixtureId, kind);
        if (targets.Count < 2) return [];   // one channel: an ordinary handle does
        var placed = PlaceGroup(clip, at, targets.Select(t => (t, (byte?)null)).ToList(), kind == HandleGroupKind.Colour ? "Add colour handle" : "Add position handle");
        SelectPoints(placed, additive: false);
        return placed;
    }

    /// <summary>
    /// Snapshot (VenueMagic): the levels set by hand (the Control and Channels screens' faders) on these channels, as
    /// handles at a time on the timeline: grouped, so the look moves as one. At the clip's end it sets the end
    /// handles. Nothing for a time outside the clip. One undo step.
    /// </summary>
    public IReadOnlyList<CurvePoint> Snapshot(LightingClip clip, TimeSpan time, IEnumerable<LaneTarget> targets)
    {
        if (clip.Locked || clip.Effect is not null) return [];
        var at = time - clip.Start;
        if (at <= TimeSpan.Zero || at > clip.Length) return [];
        var levels = targets.Select(t => (Target: t, Level: ManualLevel(t))).Where(l => l.Level is not null).ToList();
        if (levels.Count == 0) return [];
        var placed = PlaceGroup(clip, at, levels, "Snapshot", group: at < clip.Length && levels.Count > 1);
        SelectPoints(placed, additive: false);
        return placed;
    }

    /// <summary>The level a channel is set to by hand (the manual layer), or null if it isn't patched.</summary>
    public byte? ManualLevel(LaneTarget target)
    {
        int universe, address;
        if (target.FixtureId is not { } id)
            (universe, address) = (target.Universe, target.Channel ?? 0);
        else
        {
            if (_session.Patch.Fixtures.FirstOrDefault(f => f.Id == id) is not { } fixture) return null;
            universe = fixture.Universe;
            address = target.Control is { } control
                ? fixture.TryGetAddress(control, out int at) ? at : 0
                : target.Channel is { } n && n >= 1 && n <= fixture.ChannelCount ? fixture.AddressOfChannel(n) : 0;
        }
        if (address is < 1 or > 512) return null;
        var values = new byte[512];
        return _session.Engine.TryCopyManual(universe, values) ? values[address - 1] : null;
    }

    /// <summary>Puts handles on channels at one time (reusing any already there), as one undo step; grouped unless told otherwise.</summary>
    List<CurvePoint> PlaceGroup(LightingClip clip, TimeSpan at, IReadOnlyList<(LaneTarget Target, byte? Level)> levels, string description, bool group = true)
    {
        var startLevels = StartLevels(clip);
        var id = group ? Guid.NewGuid() : (Guid?)null;
        var placed = new List<CurvePoint>();
        Edit(description, () =>
        {
            foreach (var (target, level) in levels)
            {
                var point = PlaceHandle(clip, target, at, level, startLevels);
                if (!clip.IsEndHandle(point)) point.Group = id;
                placed.Add(point);
            }
        }, roots: clip);
        return placed;
    }

    /// <summary>A handle on a channel's envelope at a time in the clip: the one already there, or a new one (at the level the envelope has there, unless given one).</summary>
    static CurvePoint PlaceHandle(LightingClip clip, LaneTarget target, TimeSpan at, byte? level, Func<LaneTarget, byte> startLevels)
    {
        byte start = startLevels(target);
        byte current = Envelope.Round(Envelope.ValueAt(clip.EnvelopeFor(target, start), at));
        if (clip.LaneFor(target) is not { } lane)
        {
            lane = new ChannelLane { Target = target, Points = [new CurvePoint { Time = clip.Length, Value = start }] };
            clip.Lanes.Add(lane);
        }
        if (lane.Points.FirstOrDefault(p => p.Time == at) is { } existing)
        {
            if (level is { } value) existing.Value = value;
            return existing;
        }
        var point = new CurvePoint { Time = at, Value = level ?? current, Curve = lane.Points.FirstOrDefault(p => p.Time > at)?.Curve ?? CurveKind.Linear };
        lane.Points.Add(point);
        lane.Sort();
        return point;
    }

    /// <summary>Sets grouped handles' levels (those given one); typing or dragging in a picker merges into one undo step.</summary>
    internal void SetGroupLevels(IReadOnlyList<CurvePoint> points, Func<CurvePoint, byte?> level, string description, object mergeKey)
    {
        var changes = points.Select(p => (Point: p, Level: level(p)))
            .Where(c => c.Level is { } v && v != c.Point.Value && Locate(c.Point) is { Clip.Locked: false }).ToList();
        if (changes.Count == 0) return;
        // Every lane of the group, changed or not: merged edits (pan, then tilt) keep the first one's undo state.
        EditLanes(description, points.Select(LaneOf).OfType<ChannelLane>(),
                  () => { foreach (var (point, value) in changes) point.Value = value!.Value; }, mergeKey);
    }

    /// <summary>Separates grouped handles: each moves on its own again. One undo step.</summary>
    public void Ungroup(IEnumerable<CurvePoint> points)
    {
        var grouped = points.Where(p => p.Group is not null && Locate(p) is { Clip.Locked: false }).ToList();
        if (grouped.Count == 0) return;
        EditLanes("Ungroup handles", grouped.Select(LaneOf).OfType<ChannelLane>(), () => { foreach (var p in grouped) p.Group = null; });
    }

    /// <summary>After the handle selection changes (or an edit): the group panel, when the selection is one whole group.</summary>
    void HandleGroupSelectionChanged()
    {
        var points = _selectedPoints.ToList();
        Guid? group = points.Count > 1 && points[0].Group is { } g && points.All(p => p.Group == g) ? g : null;
        if (group is null || GroupOf(points[0]).Count != points.Count)
        {
            SelectedHandleGroup = null;
            return;
        }
        if (SelectedHandleGroup is { } shown && shown.Group == group && shown.Points.ToHashSet().SetEquals(points))
        {
            shown.Refresh();
            return;
        }
        SelectedHandleGroup = new HandleGroupViewModel(points, group.Value, this);
    }
}

/// <summary>The selected group of handles: its colour (a colour handle) or pan and tilt (a position handle), and Ungroup.</summary>
public sealed partial class HandleGroupViewModel : ObservableObject
{
    readonly TimelineEditorViewModel _editor;
    readonly Dictionary<CurvePoint, ChannelFunction?> _functions;

    public HandleGroupViewModel(IReadOnlyList<CurvePoint> points, Guid group, TimelineEditorViewModel editor)
    {
        Points = points;
        Group = group;
        _editor = editor;
        _functions = points.ToDictionary(p => p, p => editor.LaneOf(p) is { } lane ? editor.FunctionOf(lane.Target) : null);
        var fixtures = points.Select(p => editor.LaneOf(p)?.Target.FixtureId).Distinct().ToList();
        bool oneFixture = fixtures.Count == 1 && fixtures[0] is not null;
        Kind = oneFixture && _functions.Values.All(f => f is { } c && LightingEffects.IsColour(c)) ? HandleGroupKind.Colour
             : oneFixture && _functions.Values.All(f => f is ChannelFunction.Pan or ChannelFunction.Tilt) ? HandleGroupKind.Position
             : HandleGroupKind.Look;
        Fixture = oneFixture ? editor.FixtureName(fixtures[0]!.Value) ?? "Missing fixture" : null;
    }

    public IReadOnlyList<CurvePoint> Points { get; }
    public Guid Group { get; }
    public HandleGroupKind Kind { get; }
    public string? Fixture { get; }

    public bool IsColour => Kind == HandleGroupKind.Colour;
    public bool IsPosition => Kind == HandleGroupKind.Position;

    public string Heading => Kind switch
    {
        HandleGroupKind.Colour => $"Colour handle · {Fixture}",
        HandleGroupKind.Position => $"Position handle · {Fixture}",
        _ => $"Snapshot · {Points.Count} handles moving together",
    };

    byte? LevelOf(ChannelFunction function) => Points.FirstOrDefault(p => _functions[p] == function)?.Value;

    /// <summary>The colour the handles make: red, green and blue, with white added to all three.</summary>
    public Avalonia.Media.Color Colour
    {
        get
        {
            int white = LevelOf(ChannelFunction.White) ?? 0;
            byte Part(ChannelFunction f) => (byte)Math.Min(255, (LevelOf(f) ?? 0) + white);
            return Avalonia.Media.Color.FromRgb(Part(ChannelFunction.Red), Part(ChannelFunction.Green), Part(ChannelFunction.Blue));
        }
        set
        {
            var rgb = new LightingEffects.Rgb(value.R / 255.0, value.G / 255.0, value.B / 255.0);
            _editor.SetGroupLevels(Points, p => _functions[p] is { } f ? Envelope.Round(LightingEffects.Component(rgb, f) * 255) : null,
                                   "Change colour", (Group, "colour"));
        }
    }

    /// <summary>Pan and tilt, 0-255 (DMX).</summary>
    public double Pan
    {
        get => LevelOf(ChannelFunction.Pan) ?? 0;
        set => SetLevel(ChannelFunction.Pan, value, "Change pan");
    }

    public double Tilt
    {
        get => LevelOf(ChannelFunction.Tilt) ?? 0;
        set => SetLevel(ChannelFunction.Tilt, value, "Change tilt");
    }

    public bool HasPan => LevelOf(ChannelFunction.Pan) is not null;
    public bool HasTilt => LevelOf(ChannelFunction.Tilt) is not null;

    void SetLevel(ChannelFunction function, double value, string description) =>
        _editor.SetGroupLevels(Points, p => _functions[p] == function ? Envelope.Round(value) : null, description, (Group, "position"));

    [RelayCommand]
    void Ungroup() => _editor.Ungroup(Points);

    public void Refresh() => OnPropertyChanged(string.Empty);
}
