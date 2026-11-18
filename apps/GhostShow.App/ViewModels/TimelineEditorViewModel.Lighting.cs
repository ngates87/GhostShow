using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Timeline;

namespace GhostShow.App.ViewModels;

/// <summary>One entry of the effects list, dragged onto a lighting track. No kind: a Level Control clip, or a tilt move.</summary>
public sealed record EffectChoice(string Name, string Description, EffectKind? Kind, TimeSpan Length, bool IsTilt = false, bool IsHome = false);

/// <summary>A track's row in the lanes area. The header list and the canvas both lay out from these, so they always line up.</summary>
public sealed record TimelineRow(int TrackIndex, TrackRowViewModel Track, double Top, double Height)
{
    public double Bottom => Top + Height;
}

/// <summary>
/// Lighting tracks, the way VenueMagic's lamp tracks work: the channels each track controls (right-click →
/// Channels…), and its Level Control clips (<see cref="LightingClip"/>), each with an envelope per channel that
/// carries on from the previous clip. Collapsed, a track shows its clips as bars; expanded, each clip draws its
/// envelopes inside itself (every channel in its colour) with their handles, and the held levels between clips.
/// Handles are added from a clip's right-click menu (Add Envelope Handle ▸), dragged (not past their neighbours; an
/// end handle only up and down), given a curve type (Step, Linear, Spline) and deleted. Clips can be locked, linked
/// (they move together) and split at the playhead; resizing one scales or crops its envelopes.
/// Handle times are shown and edited as timeline times; in the model they're relative to their clip's start.
/// </summary>
public sealed partial class TimelineEditorViewModel
{
    public const double CollapsedTrackHeight = 34, CollapsedLightingHeight = 64, ExpandedTrackHeight = 150, AudioTrackHeight = 64, EventTrackHeight = 72;

    readonly HashSet<Guid> _expanded = [];
    readonly HashSet<CurvePoint> _selectedPoints = [];

    /// <summary>Every row of the lanes area, top to bottom.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TimelineRow> Rows { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlySet<CurvePoint> SelectedPoints { get; private set; } = new HashSet<CurvePoint>();

    /// <summary>Exact values for the selected handle, when exactly one handle (and no clip) is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPropertiesHint))]
    public partial PointPropertiesViewModel? PointProperties { get; private set; }

    /// <summary>The channel picker for a lighting track, while it's open (it takes the properties panel).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPropertiesHint))]
    public partial ChannelPickerViewModel? ChannelPicker { get; private set; }

    /// <summary>A track's properties (right-click its name → Properties…), while open; it takes the properties panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPropertiesHint))]
    public partial TrackPropertiesViewModel? TrackProperties { get; private set; }

    /// <summary>Nothing to show in the properties panel: show how to use the editor instead.</summary>
    public bool ShowPropertiesHint => Properties is null && PointProperties is null && ChannelPicker is null && TrackProperties is null;

    public double ContentHeight => Rows.Count == 0 ? 0 : Rows[^1].Bottom;

    // ---- layout ----

    internal bool IsExpanded(TimelineTrack track) => _expanded.Contains(track.Id);

    internal void SetExpanded(TrackRowViewModel row, bool expanded)
    {
        if (expanded) _expanded.Add(row.Track.Id);
        else _expanded.Remove(row.Track.Id);
        Relayout();
    }

    /// <summary>
    /// A track's height: a lighting track collapsed has room for a preview of its clips' envelopes; expanded, it's
    /// tall enough to edit them.
    /// </summary>
    public static double HeightOf(TimelineTrack track, bool expanded) => track.Kind switch
    {
        TrackKind.Lighting => expanded ? ExpandedTrackHeight : CollapsedLightingHeight,
        TrackKind.Audio => AudioTrackHeight,
        TrackKind.Event => EventTrackHeight,
        _ => CollapsedTrackHeight,
    };

    public void Relayout()
    {
        var rows = new List<TimelineRow>();
        double top = 0;
        for (int i = 0; i < Tracks.Count; i++)
        {
            var track = Tracks[i];
            rows.Add(new TimelineRow(i, track, top, track.Height));
            top += track.Height;
        }
        Rows = rows;
        OnPropertyChanged(nameof(ContentHeight));
        Revision++;
    }

    // ---- names and colours ----

    /// <summary>"Entry Par 1 · Red", "Fogger · Ch 2: Fog", or "U1 · 37" for a raw channel.</summary>
    public string LaneLabel(LaneTarget target)
    {
        if (target.FixtureId is not { } id) return $"U{target.Universe} · {target.Channel}";
        string fixture = FixtureName(id) ?? "Missing fixture";
        if (target.Control is { } control) return $"{fixture} · {control}";
        var patched = _session.Patch.Fixtures.FirstOrDefault(f => f.Id == id);
        string? channelName = target.Channel is { } n && patched is not null && n >= 1 && n <= patched.Mode.Channels.Count ? patched.Mode.Channels[n - 1].Name : null;
        return channelName is null ? $"{fixture} · Ch {target.Channel}" : $"{fixture} · Ch {target.Channel}: {channelName}";
    }

    static readonly string[] Palette = ["#4DD0E1", "#BA68C8", "#AED581", "#FF8A65", "#F06292", "#7986CB", "#4DB6AC", "#DCE775"];

    /// <summary>The lane's own colour, or one that says what it is (red for Red, ...), or a stable pick from a palette.</summary>
    public static string LaneColor(ChannelLane lane) => lane.Color ?? lane.Target.Control switch
    {
        ChannelFunction.Intensity => "#FFD54F",
        ChannelFunction.Red => "#FF5252",
        ChannelFunction.Green => "#66BB6A",
        ChannelFunction.Blue => "#42A5F5",
        ChannelFunction.White => "#ECEFF1",
        ChannelFunction.Amber => "#FFB300",
        ChannelFunction.UV => "#B388FF",
        _ => Palette[(int)((uint)lane.Target.GetHashCode() % Palette.Length)],
    };

    IEnumerable<ChannelLane> AllLanes() => (SelectedTimeline?.Tracks ?? [])
        .SelectMany(t => t.Channels.Concat(t.Events.OfType<LightingClip>().SelectMany(c => c.Lanes)));

    // Which envelope and clip each handle is in, and each track's channels resolved across its clips. The canvas asks
    // for every handle it draws, so these are indexes rather than searches. Rebuilt after any edit (Revision changes)
    // and whenever a lookup misses, so they're never wrong, only occasionally rebuilt.
    Dictionary<CurvePoint, (ChannelLane Lane, LightingClip Clip)> _pointIndex = new();
    Dictionary<ChannelLane, LightingClip> _laneIndex = new();
    Dictionary<TimelineTrack, IReadOnlyList<TrackLevels>> _levels = new();
    long _indexedRevision = -1;
    ShowTimeline? _indexedTimeline;

    void EnsureIndex(bool force = false)
    {
        if (!force && _indexedRevision == Revision && _indexedTimeline == SelectedTimeline) return;
        var points = new Dictionary<CurvePoint, (ChannelLane, LightingClip)>(ReferenceEqualityComparer.Instance);
        var lanes = new Dictionary<ChannelLane, LightingClip>(ReferenceEqualityComparer.Instance);
        foreach (var track in SelectedTimeline?.Tracks ?? [])
            foreach (var clip in track.Events.OfType<LightingClip>())
                foreach (var lane in clip.Lanes)
                {
                    lanes[lane] = clip;
                    foreach (var p in lane.Points) points[p] = (lane, clip);
                }
        (_pointIndex, _laneIndex, _indexedRevision, _indexedTimeline) = (points, lanes, Revision, SelectedTimeline);
        _levels = new Dictionary<TimelineTrack, IReadOnlyList<TrackLevels>>(ReferenceEqualityComparer.Instance);
        _lights = new Dictionary<TimelineTrack, IReadOnlyList<LightPreview.Light>>(ReferenceEqualityComparer.Instance);
    }

    Dictionary<TimelineTrack, IReadOnlyList<LightPreview.Light>> _lights = new();

    /// <summary>A lighting track's lights (its fixtures, by the channels that make light), for its colour strip and header.</summary>
    public IReadOnlyList<LightPreview.Light> LightsOf(TimelineTrack track)
    {
        var levels = LevelsOf(track);   // brings the index up to date first
        if (_lights.TryGetValue(track, out var lights)) return lights;
        var patched = _session.Patch.Fixtures.ToDictionary(f => f.Id);
        return _lights[track] = LightPreview.LightsOf(levels, target => TimelineCompiler.FunctionOf(target, patched));
    }

    (ChannelLane Lane, LightingClip Clip)? Locate(CurvePoint point)
    {
        EnsureIndex();
        if (_pointIndex.TryGetValue(point, out var where)) return where;
        EnsureIndex(force: true);   // added since the last edit (e.g. mid-edit): look again
        return _pointIndex.TryGetValue(point, out where) ? where : null;
    }

    public ChannelLane? LaneOf(CurvePoint point) => Locate(point)?.Lane;

    /// <summary>The clip an envelope belongs to.</summary>
    public LightingClip? ClipOf(ChannelLane lane)
    {
        EnsureIndex();
        if (_laneIndex.TryGetValue(lane, out var clip)) return clip;
        EnsureIndex(force: true);
        return _laneIndex.GetValueOrDefault(lane);
    }

    public LightingClip? ClipOf(CurvePoint point) => Locate(point)?.Clip;

    /// <summary>A handle's time on the timeline (its clip's start plus its time in the clip).</summary>
    public TimeSpan AbsoluteTime(CurvePoint point) => Locate(point) is { Clip: { } clip } ? clip.Start + point.Time : point.Time;

    /// <summary>A lighting track's channels as they play across its clips (one per channel, in the track's order).</summary>
    public IReadOnlyList<TrackLevels> LevelsOf(TimelineTrack track)
    {
        EnsureIndex();
        if (_levels.TryGetValue(track, out var levels)) return levels;
        var patched = _session.Patch.Fixtures.ToDictionary(f => f.Id);
        return _levels[track] = TrackLevels.ResolveAll(track, target => TimelineCompiler.FunctionOf(target, patched),
                                                       target => TimelineCompiler.TiltOf(target, patched));
    }

    TimelineTrack? TrackOf(LightingClip clip) => SelectedTimeline?.Tracks.FirstOrDefault(t => t.Events.Contains(clip));

    /// <summary>Each channel's start handle level in a clip (where the previous clip leaves it): what resizing and splitting need.</summary>
    Func<LaneTarget, byte> StartLevels(LightingClip clip)
    {
        var levels = TrackOf(clip) is { } track ? LevelsOf(track) : [];
        return target => levels.FirstOrDefault(l => l.Channel.Target == target)?.StartLevelOf(clip) ?? 0;
    }

    /// <summary>The first clip of a channel whose start level is its own (the channel starts at its first clip); null if none.</summary>
    public LightingClip? FirstClipOf(TrackLevels levels) =>
        levels.Channel.Start != ChannelStart.Fixed && levels.Clips.Count > 0 ? levels.Clips[0].Clip : null;

    // ---- handle selection (called by the canvas) ----

    /// <summary>Click on a handle: select it alone, or with additive (Ctrl) toggle it in the selection. A grouped handle brings its group.</summary>
    public void SelectPoint(CurvePoint point, bool additive)
    {
        if (!additive) _selectedPoints.Clear();
        _selection.Clear();
        var group = GroupOf(point);
        if (additive && _selectedPoints.Contains(point)) _selectedPoints.ExceptWith(group);
        else _selectedPoints.UnionWith(group);
        SelectionChanged();
    }

    void PointSelectionChanged()
    {
        SelectedPoints = new HashSet<CurvePoint>(_selectedPoints);
        var single = _selection.Count == 0 && _selectedPoints.Count == 1 ? _selectedPoints.First() : null;
        if (PointProperties?.Point != single)
            PointProperties = single is null || Locate(single) is not { } where ? null : new PointPropertiesViewModel(single, where.Lane, where.Clip, this);
        else
            PointProperties?.Refresh();
        HandleGroupSelectionChanged();
    }

    // ---- handle edits ----

    /// <summary>
    /// Right-click a clip → Add Envelope Handle ▸ channel: a handle on that channel's envelope at the time (snapped),
    /// at the level the envelope already has there, so nothing changes until it's dragged. A channel without an
    /// envelope in the clip gets one (holding its level to an end handle). Null if the time isn't inside the clip.
    /// </summary>
    public CurvePoint? AddHandle(LightingClip clip, LaneTarget target, TimeSpan time)
    {
        if (clip.Locked || clip.IsGenerated) return null;   // an effect's or move's envelopes are made from its settings
        var at = SnapTime(time) - clip.Start;
        if (at <= TimeSpan.Zero || at >= clip.Length) at = time - clip.Start;   // the grid would put it on an edge
        if (at <= TimeSpan.Zero || at >= clip.Length) return null;
        byte start = StartLevels(clip)(target);
        var lane = clip.LaneFor(target);
        var point = new CurvePoint { Time = at, Value = Envelope.Round(Envelope.ValueAt(clip.EnvelopeFor(target, start), at)) };
        if (lane is null)
        {
            var newLane = new ChannelLane { Target = target, Points = [point, new CurvePoint { Time = clip.Length, Value = start }] };
            Edit("Add envelope handle", () => clip.Lanes.Add(newLane), roots: Only(clip));
        }
        else
        {
            // Keep the curve the segment had: the new handle and the one after it get its curve type.
            var next = lane.Points.FirstOrDefault(p => p.Time > at);
            point.Curve = next?.Curve ?? CurveKind.Linear;
            EditLanes("Add envelope handle", [lane], () => lane.Points.Add(point));
        }
        _selectedPoints.Clear();
        _selection.Clear();
        _selectedPoints.Add(point);
        SelectionChanged();
        return point;
    }

    /// <summary>"Add" on a lighting track: a Level Control clip at the time, every channel holding its level (so it changes nothing until edited).</summary>
    void AddClipAt(TrackRowViewModel row, TimeSpan time)
    {
        var track = row.Track;
        if (track.Channels.Count == 0)
        {
            EditChannels(row);
            return;
        }
        var clip = new LightingClip { Name = LightingClip.DefaultName, Start = time };
        Edit("Add Level Control clip", () => track.Events.Add(clip), roots: Only(track));
        _selectedPoints.Clear();
        _selection.Clear();
        _selection.Add(clip.Id);
        SelectionChanged();
    }

    // ---- built-in effects ----

    /// <summary>Adds a built-in effect clip (Lightning, Fire, …) at a time, at the effect's usual length, with a fresh random pattern.</summary>
    public LightingClip? AddEffectAt(TrackRowViewModel row, TimeSpan time, EffectKind kind)
    {
        var track = row.Track;
        if (!row.IsLighting) return null;
        if (track.Channels.Count == 0)
        {
            EditChannels(row);
            return null;
        }
        var info = EffectInfo.Of(kind);
        var clip = new LightingClip { Name = info.Name, Start = time, Length = info.DefaultLength, Effect = LightingEffect.Create(kind, Random.Shared.Next()) };
        Edit(info.IsLook ? "Add look" : $"Add {info.Name} effect", () => track.Events.Add(clip), roots: Only(track));
        _selectedPoints.Clear();
        _selection.Clear();
        _selection.Add(clip.Id);
        SelectionChanged();
        return clip;
    }

    /// <summary>
    /// What the effects list offers: Level Control, a look, the moving-head moves, then the built-in effects
    /// (VenueMagic's library of fixture effects).
    /// </summary>
    public static IReadOnlyList<EffectChoice> EffectChoices { get; } =
    [
        new(LightingClip.DefaultName, "Handles you set yourself", null, new LightingClip().Length),
        Choice(EffectInfo.Of(EffectKind.Look)),
        new(LightingClip.TiltName, "Moving heads tilt from one angle to another over the clip (for a track with moving heads)", null, LightingClip.DefaultLength, IsTilt: true),
        new(LightingClip.HomeName, "Moving heads go home (their rest position) with their light off, then it comes back (for a track with moving heads)", null, TimeSpan.FromSeconds(2), IsTilt: true, IsHome: true),
        .. EffectInfo.All.Where(info => !info.IsLook).Select(Choice),
    ];

    static EffectChoice Choice(EffectInfo info) => new(info.Name, info.Description, info.Kind, info.DefaultLength);

    /// <summary>A choice from the effects list, let go over a lighting track.</summary>
    public void DropEffect(EffectChoice choice, TrackRowViewModel row, TimeSpan time)
    {
        if (choice.Kind is { } kind) AddEffectAt(row, time, kind);
        else if (choice.IsTilt) AddTiltAt(row, time, choice.IsHome);
        else AddClipAt(row, time);
    }

    /// <summary>Asks the screen to scroll a time into view. The Effects menu adds at the playhead, which may be out of view.</summary>
    public event Action<TimeSpan>? RevealRequested;

    /// <summary>
    /// A click on an entry of the Effects menu: the clip on the selected lighting track (or the first one), at the next
    /// open slot: at the playhead if it fits there, otherwise in the first gap after it that's long enough.
    /// </summary>
    [RelayCommand]
    void AddChoice(EffectChoice choice)
    {
        if (EffectsTrack() is not { } row) return;
        var at = NextOpenSlot(row.Track, SnapTime(_playback.Position), choice.Length);
        DropEffect(choice, row, at);
        RevealRequested?.Invoke(at);
    }

    [RelayCommand]
    void AddEffect(EffectKind kind) => AddChoice(EffectChoices.First(c => c.Kind == kind));

    [RelayCommand]
    void AddLevelControl() => AddChoice(EffectChoices.First(c => c.Kind is null));

    /// <summary>The earliest time from <paramref name="from"/> where a clip this long overlaps none already on the track.</summary>
    public TimeSpan NextOpenSlot(TimelineTrack track, TimeSpan from, TimeSpan length)
    {
        var taken = track.Events.Select(e => (Start: e.Start, End: e.Start + LengthOf(e))).OrderBy(c => c.Start).ToList();
        var at = from;
        foreach (var (start, end) in taken)
        {
            if (end <= at) continue;          // before the slot
            if (start >= at + length) break;  // after it: it fits
            at = end;                         // in the way: after it
        }
        return at;
    }

    // ---- tilt moves (moving heads) ----

    /// <summary>
    /// A tilt clip at a time: the track's moving heads sweep from -45° to +45° over it, to change in its properties; or,
    /// home, go to their rest position in the dark. A track whose moving heads' tilt channels aren't on it yet gets them
    /// (fine ones too); a track with no moving heads gets nothing, with a notice.
    /// </summary>
    public LightingClip? AddTiltAt(TrackRowViewModel row, TimeSpan time, bool home = false)
    {
        if (!row.IsLighting) return null;
        var missing = MissingTiltChannels(row.Track);
        if (!HasTiltChannel(row.Track) && missing.Count == 0)
        {
            Notice?.Invoke($"{row.Name} has no moving heads to tilt: add a fixture that tilts to it (Channels…) first.");
            return null;
        }
        if (missing.Count > 0)
        {
            SetTrackChannels(row.Track, [.. row.Track.Channels.Select(c => c.Target), .. missing]);
            Notice?.Invoke($"Added the moving heads' tilt channels to {row.Name}.");
        }
        var clip = home
            ? new LightingClip { Name = LightingClip.HomeName, Start = time, Length = TimeSpan.FromSeconds(2), Tilt = new TiltMove { Home = true } }
            : new LightingClip { Name = LightingClip.TiltName, Start = time, Tilt = new TiltMove { StartDegrees = -45, EndDegrees = 45 } };
        Edit(home ? "Add home move" : "Add tilt move", () => row.Track.Events.Add(clip), roots: Only(row.Track));
        _selectedPoints.Clear();
        _selection.Clear();
        _selection.Add(clip.Id);
        SelectionChanged();
        return clip;
    }

    /// <summary>Changes a tilt clip's move (one undoable edit; dragging or typing in one field merges).</summary>
    public void ChangeTilt(LightingClip clip, string description, Action<TiltMove> change, string field)
    {
        if (clip.Tilt is not { } current || clip.Locked) return;
        var changed = current.Clone();
        change(changed);
        if ((changed.StartDegrees, changed.EndDegrees, changed.Dark, changed.Home) == (current.StartDegrees, current.EndDegrees, current.Dark, current.Home)) return;
        Edit(description, () => clip.Tilt = changed, (clip.Id, "tilt:" + field), Only(clip));
    }

    /// <summary>
    /// How far a tilt clip's moving heads tilt each way from straight ahead (half the widest travel among its track's
    /// tilt channels: ±115° for a 230° bar); null if its track has no tilt channel.
    /// </summary>
    public double? TiltLimitOf(LightingClip clip)
    {
        if (TrackOf(clip) is not { } track) return null;
        var tilts = LevelsOf(track).Select(l => l.Tilt).OfType<TiltChannel>().Where(t => !t.IsFine).ToList();
        return tilts.Count == 0 ? null : tilts.Max(t => t.Range) / 2;
    }

    /// <summary>Whether a track has a moving head's tilt channel on it.</summary>
    public bool HasTiltChannel(TimelineTrack track) => LevelsOf(track).Any(l => l.Tilt is not null);

    /// <summary>Whether a tilt clip can go on a track: it has a tilt channel, or a moving head whose tilt channels can be added.</summary>
    public bool CanTilt(TimelineTrack track) => HasTiltChannel(track) || MissingTiltChannels(track).Count > 0;

    /// <summary>The Effects menu offers Tilt and Home only when the track it adds to (the selected lighting one) can tilt.</summary>
    public bool CanTiltHere => EffectsTrack() is { } row && CanTilt(row.Track);

    /// <summary>The Effects menu is opening: whether it can offer Tilt depends on the selected track as it is now.</summary>
    public void RefreshEffectChoices() => OnPropertyChanged(nameof(CanTiltHere));

    /// <summary>
    /// The tilt channels (coarse and fine) of the moving heads a track has channels of, but not their tilt: what a tilt
    /// clip adds. A fixture with any tilt channel on the track already is left as it is.
    /// </summary>
    IReadOnlyList<LaneTarget> MissingTiltChannels(TimelineTrack track)
    {
        var patched = _session.Patch.Fixtures.ToDictionary(f => f.Id);
        var missing = new List<LaneTarget>();
        foreach (var id in track.Channels.Select(c => c.Target.FixtureId).OfType<Guid>().Distinct())
        {
            if (!patched.TryGetValue(id, out var fixture)) continue;
            if (track.Channels.Any(c => c.Target.FixtureId == id && TimelineCompiler.TiltOf(c.Target, patched) is not null)) continue;
            for (int n = 1; n <= fixture.Mode.Channels.Count; n++)
                if (fixture.Mode.Channels[n - 1].Function is ChannelFunction.Tilt or ChannelFunction.TiltFine)
                    missing.Add(LaneTarget.FixtureChannel(id, n));
        }
        return missing;
    }

    TrackRowViewModel? EffectsTrack() => SelectedTrack is { IsLighting: true } selected ? selected : Tracks.FirstOrDefault(t => t.IsLighting);

    /// <summary>Changes an effect clip's settings (one undoable edit; typing into one field merges).</summary>
    public void ChangeEffect(LightingClip clip, string description, Action<LightingEffect> change, string field)
    {
        if (clip.Effect is not { } current || clip.Locked) return;
        var changed = current.Clone();
        change(changed);
        if (EffectEquals(current, changed)) return;
        Edit(description, () => clip.Effect = changed, (clip.Id, "effect:" + field), Only(clip));
    }

    static bool EffectEquals(LightingEffect a, LightingEffect b) =>
        (a.Kind, a.Strength, a.Rate, a.Color, a.Seed, a.Ending) == (b.Kind, b.Strength, b.Rate, b.Color, b.Seed, b.Ending);

    /// <summary>The same effect with a new random pattern (other strike times, another flicker).</summary>
    public void NewEffectPattern(LightingClip clip) => ChangeEffect(clip, "New random pattern", e => e.Seed = Random.Shared.Next(), "seed-" + Guid.NewGuid());

    /// <summary>
    /// Turns an effect clip into a Level Control clip with the effect's envelopes as its own handles, to fine-tune by
    /// hand (as it plays now: from where the previous clip leaves each channel). One undo step.
    /// </summary>
    public void ConvertToLevelControl(LightingClip clip)
    {
        if (clip.Effect is not { } effect || clip.Locked || TrackOf(clip) is not { } track) return;
        var patched = _session.Patch.Fixtures.ToDictionary(f => f.Id);
        ChannelFunction? FunctionOf(LaneTarget t) => TimelineCompiler.FunctionOf(t, patched);
        var keys = LightingEffects.Keys(effect, clip.Length);
        var roles = LightingEffects.Roles(track.Channels, FunctionOf);
        var levels = LevelsOf(track);
        var lanes = new List<ChannelLane>();
        for (int i = 0; i < track.Channels.Count; i++)
        {
            var target = track.Channels[i].Target;
            if (roles[i] == EffectRole.Untouched) continue;
            byte start = levels.FirstOrDefault(l => l.Channel == track.Channels[i])?.StartLevelOf(clip) ?? track.Channels[i].StartLevel;
            var envelope = LightingEffects.EnvelopeFor(keys, roles[i], target.Control ?? FunctionOf(target), start, clip.Length);
            lanes.Add(new ChannelLane { Target = target, Points = envelope.Skip(1).Select(p => p.Clone()).ToList() });
        }
        Edit("Convert to Level Control", () =>
        {
            clip.Effect = null;
            clip.Lanes = lanes;
        }, roots: Only(clip));
    }

    /// <summary>
    /// Resizes a lighting clip from either edge (one undo step): its envelopes scale with it or are cropped (see
    /// <see cref="LightingClip.ScaleOnResize"/>). Dragging the left edge keeps the end where it is.
    /// </summary>
    public void ResizeClip(LightingClip clip, TimeSpan start, TimeSpan length)
    {
        start = start < TimeSpan.Zero ? TimeSpan.Zero : start;
        if (clip.Locked || length < TimeSpan.FromMilliseconds(50) || (start == clip.Start && length == clip.Length)) return;
        var startLevels = StartLevels(clip);
        Edit(clip.ScaleOnResize ? "Resize clip" : "Crop clip", () =>
        {
            if (start != clip.Start) clip.ResizeStart(start);
            if (clip.Length != length) clip.ResizeEnd(length, startLevels);
        }, roots: clip);
    }

    /// <summary>
    /// Where the selected handles would go if dragged by delta time and level: the grabbed handle (anchor) snaps to
    /// the grid; together they're kept between their unselected neighbours (an envelope's start and end included);
    /// end handles only change level; handles in locked clips don't move. Relative times.
    /// </summary>
    public IReadOnlyDictionary<CurvePoint, (TimeSpan Time, byte Value)> PreviewPointMove(TimeSpan deltaTime, int deltaValue, CurvePoint? anchor = null)
    {
        var result = new Dictionary<CurvePoint, (TimeSpan, byte)>(ReferenceEqualityComparer.Instance);
        var points = _selectedPoints.Where(p => Locate(p) is { Clip.Locked: false }).ToList();
        if (points.Count == 0) return result;
        var movers = points.Where(p => !Locate(p)!.Value.Clip.IsEndHandle(p)).ToHashSet();
        if (movers.Count > 0)
        {
            var grabbed = anchor is not null && movers.Contains(anchor) ? anchor : movers.First();
            var at = AbsoluteTime(grabbed);
            deltaTime = SnapTime(at + deltaTime) - at;
            foreach (var p in movers)
            {
                var lane = Locate(p)!.Value.Lane.Points;
                int i = lane.IndexOf(p);
                var previous = TimeSpan.Zero;
                for (int k = i - 1; k >= 0; k--) if (!movers.Contains(lane[k])) { previous = lane[k].Time; break; }
                var next = lane[^1].Time;
                for (int k = i + 1; k < lane.Count; k++) if (!movers.Contains(lane[k])) { next = lane[k].Time; break; }
                if (p.Time + deltaTime < previous) deltaTime = previous - p.Time;
                if (p.Time + deltaTime > next) deltaTime = next - p.Time;
            }
        }
        foreach (var p in points)
            result[p] = (movers.Contains(p) ? p.Time + deltaTime : p.Time, (byte)Math.Clamp(p.Value + deltaValue, 0, 255));
        return result;
    }

    /// <summary>A drag of selected handles (see <see cref="PreviewPointMove"/>). One undo step.</summary>
    public void MoveSelectedPoints(TimeSpan deltaTime, int deltaValue, CurvePoint? anchor = null)
    {
        if (deltaTime == TimeSpan.Zero && deltaValue == 0) return;
        var moves = PreviewPointMove(deltaTime, deltaValue, anchor);
        if (moves.All(m => m.Key.Time == m.Value.Time && m.Key.Value == m.Value.Value)) return;
        EditLanes(moves.Count == 1 ? "Move envelope handle" : $"Move {moves.Count} envelope handles", moves.Keys.Select(LaneOf).OfType<ChannelLane>(), () =>
        {
            foreach (var (p, (time, value)) in moves) (p.Time, p.Value) = (time, value);
        });
    }

    /// <summary>Deletes the selected handles, except end handles (every envelope keeps its end) and those in locked clips.</summary>
    void DeleteSelectedPoints()
    {
        var points = _selectedPoints.Where(p => Locate(p) is { Clip: { Locked: false } clip } && !clip.IsEndHandle(p)).ToList();
        if (points.Count > 0)
            EditLanes(points.Count == 1 ? "Delete envelope handle" : $"Delete {points.Count} envelope handles", points.Select(LaneOf).OfType<ChannelLane>(),
                      () => { foreach (var lane in AllLanes()) lane.Points.RemoveAll(points.Contains); });
        _selectedPoints.Clear();
        SelectionChanged();
    }

    /// <summary>How the level gets to these handles from the previous ones: Step, Linear or Spline.</summary>
    public void SetCurve(IEnumerable<CurvePoint> points, CurveKind curve)
    {
        var changing = points.Where(p => p.Curve != curve && Locate(p) is { Clip.Locked: false }).ToList();
        if (changing.Count == 0) return;
        EditLanes($"Make {(changing.Count == 1 ? "handle" : "handles")} {curve.ToString().ToLowerInvariant()}", changing.Select(LaneOf).OfType<ChannelLane>(),
                  () => { foreach (var p in changing) p.Curve = curve; });
    }

    /// <summary>
    /// Exact values from the properties panel (time on the timeline, kept between the handle's neighbours; an end
    /// handle's time doesn't change). Typing in one field merges into one undo step.
    /// </summary>
    internal void SetPoint(CurvePoint point, TimeSpan? time = null, byte? value = null, string field = "")
    {
        if (Locate(point) is not { } where || where.Clip.Locked) return;
        var (lane, clip) = where;
        var newTime = point.Time;
        if (time is { } t && !clip.IsEndHandle(point))
        {
            int i = lane.Points.IndexOf(point);
            var previous = i > 0 ? lane.Points[i - 1].Time : TimeSpan.Zero;
            var next = i + 1 < lane.Points.Count ? lane.Points[i + 1].Time : clip.Length;
            newTime = t - clip.Start < previous ? previous : t - clip.Start > next ? next : t - clip.Start;
        }
        var newValue = value ?? point.Value;
        if (newTime == point.Time && newValue == point.Value) return;
        EditLanes(time is not null ? "Move envelope handle" : "Change level", [lane], () => { point.Time = newTime; point.Value = newValue; }, (point, field));
    }

    /// <summary>A track channel's start level (its first clip's start handle; with a fixed start, held before it too).</summary>
    public void SetStartLevel(ChannelLane channel, byte level, string field = "start")
    {
        if (channel.StartLevel == level) return;
        Edit("Change start level", () => channel.StartLevel = level, (channel, field), Only(channel));
    }

    public void SetChannelStart(ChannelLane channel, ChannelStart start)
    {
        if (channel.Start == start) return;
        string description = start switch
        {
            ChannelStart.Fixed => "Start at a fixed level",
            ChannelStart.LeaveAlone => "Leave alone until the first clip",
            _ => "Start at the first clip",
        };
        Edit(description, () => channel.Start = start, roots: Only(channel));
    }

    /// <summary>
    /// An edit to handles, as one undo step: afterwards each affected envelope's handles are put back in time order.
    /// Only those envelopes are compared (cheap enough for a drag).
    /// </summary>
    void EditLanes(string description, IEnumerable<ChannelLane> lanes, Action change, object? mergeKey = null)
    {
        var affected = lanes.Distinct().ToArray<object>();
        if (affected.Length == 0) return;
        Edit(description, () =>
        {
            change();
            foreach (ChannelLane lane in affected) lane.Sort();
        }, mergeKey, affected);
    }

    // ---- clips: split, lock, link ----

    /// <summary>
    /// Splits clips at the playhead: the selected ones it's inside, or (with nothing selected) those on the selected
    /// track. Each becomes two that play as the one did (audio: the second starts further into the file). Locked
    /// clips stay whole. One undo step.
    /// </summary>
    public void SplitAtPlayhead() => SplitAt(_playback.Position);

    public void SplitAt(TimeSpan at)
    {
        var candidates = _selection.Count > 0 ? SelectedEvents() : SelectedTrack?.Track.Events ?? [];
        // Effects are made whole (a split would make two different effects): convert one first to split it.
        var splits = candidates.Where(e => !e.Locked && e is LightingClip { IsGenerated: false } or AudioEvent && e.Start < at && at < e.Start + LengthOf(e))
                               .Select(e => (Track: TrackContaining(e)!, Event: e)).ToList();
        if (splits.Count == 0) return;
        var added = new List<TimelineEvent>();
        Edit(splits.Count == 1 ? "Split clip" : $"Split {splits.Count} clips", () =>
        {
            foreach (var (track, e) in splits)
            {
                switch (e)
                {
                    case LightingClip clip:
                    {
                        var second = clip.SplitAt(at - clip.Start, StartLevels(clip))!;
                        track.Events.Add(second);
                        added.Add(second);
                        break;
                    }
                    case AudioEvent sound:
                    {
                        var cut = at - sound.Start;
                        var second = (AudioEvent)sound.Clone();
                        (second.Start, second.Offset, second.Length, second.FadeIn) = (at, sound.Offset + cut, sound.Length - cut, TimeSpan.Zero);
                        (sound.Length, sound.FadeOut) = (cut, TimeSpan.Zero);
                        track.Events.Add(second);
                        added.Add(second);
                        break;
                    }
                }
            }
        }, roots: [.. splits.Select(s => s.Track).Distinct().Select(Only), .. splits.Select(s => s.Event)]);
        _selectedPoints.Clear();
        _selection.Clear();
        foreach (var e in added) _selection.Add(e.Id);
        SelectionChanged();
    }

    /// <summary>Locks the selected clips (or unlocks them, if they're all locked already).</summary>
    public void ToggleLock()
    {
        var events = SelectedEvents().ToList();
        if (events.Count == 0) return;
        bool lockThem = events.Any(e => !e.Locked);
        Edit(lockThem ? (events.Count == 1 ? "Lock clip" : $"Lock {events.Count} clips") : (events.Count == 1 ? "Unlock clip" : $"Unlock {events.Count} clips"),
             () => { foreach (var e in events) e.Locked = lockThem; }, roots: [.. events.Select(Only)]);
    }

    public bool CanLink => _selection.Count > 1;
    public bool CanUnlink => SelectedEvents().Any(e => e.LinkGroup is not null);

    /// <summary>Links the selected clips: from now on, moving one moves them all.</summary>
    public void Link()
    {
        var events = SelectedEvents().ToList();
        if (events.Count < 2) return;
        var group = Guid.NewGuid();
        Edit($"Link {events.Count} clips", () => { foreach (var e in events) e.LinkGroup = group; }, roots: [.. events.Select(Only)]);
    }

    /// <summary>Unlinks the selected clips from whatever they were linked to.</summary>
    public void Unlink()
    {
        var events = SelectedEvents().Where(e => e.LinkGroup is not null).ToList();
        if (events.Count == 0) return;
        Edit(events.Count == 1 ? "Unlink clip" : $"Unlink {events.Count} clips", () => { foreach (var e in events) e.LinkGroup = null; }, roots: [.. events.Select(Only)]);
    }

    /// <summary>
    /// What moving these events moves: them and every clip linked to them. Empty if any of those is locked (a locked
    /// clip can't move, and neither can what's linked to it).
    /// </summary>
    public IReadOnlySet<Guid> MovingWith(IEnumerable<Guid> ids)
    {
        var events = AllEvents().ToList();
        var chosen = ids.ToHashSet();
        var groups = events.Where(e => chosen.Contains(e.Id) && e.LinkGroup is not null).Select(e => e.LinkGroup).ToHashSet();
        var moving = events.Where(e => chosen.Contains(e.Id) || (e.LinkGroup is not null && groups.Contains(e.LinkGroup))).ToList();
        return moving.Any(e => e.Locked) ? new HashSet<Guid>() : moving.Select(e => e.Id).ToHashSet();
    }

    // ---- selecting several (box select) and nudging (arrow keys) ----

    /// <summary>A box drawn over clips: selects them (adding to the selection with Ctrl).</summary>
    public void SelectEvents(IEnumerable<Guid> ids, bool additive)
    {
        if (!additive) _selection.Clear();
        _selectedPoints.Clear();
        _selection.UnionWith(ids);
        SelectionChanged();
    }

    /// <summary>A box drawn over expanded clips: selects the handles in it (adding with Ctrl).</summary>
    public void SelectPoints(IEnumerable<CurvePoint> points, bool additive)
    {
        if (!additive) _selectedPoints.Clear();
        _selection.Clear();
        _selectedPoints.UnionWith(WithGroups(points));
        SelectionChanged();
    }

    /// <summary>
    /// Arrow keys: Left/Right move the selection by one snap step (a tenth of a second with snap off); Up/Down
    /// change selected handles' level by 5 (fine: 1).
    /// </summary>
    public void Nudge(int timeSteps, int levelSteps, bool fine)
    {
        var step = Snap.Interval > TimeSpan.Zero && !fine ? Snap.Interval : TimeSpan.FromSeconds(fine ? 0.01 : 0.1);
        if (_selectedPoints.Count > 0) MoveSelectedPoints(step * timeSteps, levelSteps * (fine ? 1 : 5));
        else if (timeSteps != 0) MoveSelection(step * timeSteps, snapToGrid: !fine && Snap.Interval > TimeSpan.Zero);
    }

    /// <summary>
    /// Edge snapping for drags: the nearest of the playhead and every clip's start and end (other than the ones
    /// being dragged), if within the tolerance; otherwise null (the grid snap applies).
    /// </summary>
    public TimeSpan? SnapToEdges(TimeSpan time, TimeSpan tolerance, IReadOnlySet<Guid> exclude)
    {
        TimeSpan? best = null;
        var bestDistance = tolerance;
        void Consider(TimeSpan candidate)
        {
            var distance = (candidate - time).Duration();
            if (distance <= bestDistance) (best, bestDistance) = (candidate, distance);
        }
        Consider(_playback.Position);
        foreach (var e in (SelectedTimeline?.Tracks ?? []).SelectMany(t => t.Events).Where(e => !exclude.Contains(e.Id)))
        {
            Consider(e.Start);
            if (LengthOf(e) is var length && length > TimeSpan.Zero) Consider(e.Start + length);
        }
        return best;
    }

    // ---- the Envelope Editor window ----

    /// <summary>The Envelope Editor, while its window is open.</summary>
    [ObservableProperty]
    public partial EnvelopeEditorViewModel? EnvelopeEditor { get; private set; }

    /// <summary>Asks the view to show (or bring to the front) the Envelope Editor window.</summary>
    public event Action? EnvelopeEditorRequested;

    /// <summary>Double-click a lighting clip (or right-click → Envelope Editor…): edit its envelopes in their own window.</summary>
    public void OpenEnvelopeEditor(LightingClip clip)
    {
        if (!_selection.Contains(clip.Id) || _selection.Count != 1) SelectEvent(clip.Id, additive: false);
        EnvelopeEditor ??= new EnvelopeEditorViewModel(this);
        EnvelopeEditor.Show(clip);
        EnvelopeEditorRequested?.Invoke();
    }

    /// <summary>The toolbar's Envelopes… button: for the selected lighting clip, or the one a selected handle is in.</summary>
    [RelayCommand]
    void OpenEnvelopes()
    {
        if (ClipForEnvelopeEditor() is { } clip) OpenEnvelopeEditor(clip);
    }

    /// <summary>The window was closed.</summary>
    public void CloseEnvelopeEditor() => EnvelopeEditor = null;

    /// <summary>The lighting clip the selection is about: the one selected clip, or the one clip every selected handle is in.</summary>
    LightingClip? ClipForEnvelopeEditor()
    {
        if (_selection.Count == 1 && FindEvent(_selection.First()) is LightingClip clip) return clip;
        var clips = _selectedPoints.Select(ClipOf).Distinct().ToList();
        return _selection.Count == 0 && clips.Count == 1 ? clips[0] : null;
    }

    /// <summary>The Envelope Editor follows the selection to another lighting clip, and its handles' changes.</summary>
    void EnvelopeEditorFollowSelection()
    {
        if (EnvelopeEditor is not { } envelopes) return;
        if (ClipForEnvelopeEditor() is { } clip && clip != envelopes.Clip) envelopes.Show(clip);
        envelopes.AfterHandleChange();
    }

    // ---- channels and track properties ----

    /// <summary>Goes to Lighting → Fixtures (set by the main window), for the picker's "patch fixtures first".</summary>
    public Action? ShowFixtures { get; set; }

    /// <summary>Right-click → Channels…: opens the picker in the properties panel.</summary>
    public void EditChannels(TrackRowViewModel row)
    {
        if (!row.IsLighting) return;
        SelectedTrack = row;
        TrackProperties = null;
        // A new track still called "Lighting 2" takes its fixtures' name with its first channels.
        bool rename = row.Track.Channels.Count == 0 && DefaultLightingName().IsMatch(row.Track.Name ?? "");
        ChannelPicker = new ChannelPickerViewModel(row.Track, _session, this, ShowFixtures, targets =>
        {
            SetTrackChannels(row.Track, targets, rename ? NameFor(targets) : null);
            ChannelPicker = null;
        }, () => ChannelPicker = null);
        // Where it can be seen: the inspector open, on its selection tab.
        IsInspectorOpen = true;
        IsTimelineTab = false;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^Lighting( \d+)?$")]
    private static partial System.Text.RegularExpressions.Regex DefaultLightingName();

    /// <summary>A track's name from the fixtures it controls: one's name, or "A + B"; null for none or more than two.</summary>
    string? NameFor(IReadOnlyList<LaneTarget> targets)
    {
        var names = targets.Select(t => t.FixtureId).OfType<Guid>().Distinct().Select(id => FixtureName(id)).OfType<string>().ToList();
        return names.Count switch { 1 => names[0], 2 => $"{names[0]} + {names[1]}", _ => null };
    }

    /// <summary>Right-click → Properties…: the track's name, options and (lighting) each channel's start level.</summary>
    public void EditTrackProperties(TrackRowViewModel row)
    {
        SelectedTrack = row;
        ChannelPicker = null;
        ClearSelection();   // the panel shows one thing: now the track
        TrackProperties = NewTrackProperties(row);
    }

    /// <summary>A track's properties; closing them shows the Timeline tab (the selection tab keeps the track, a click away).</summary>
    TrackPropertiesViewModel NewTrackProperties(TrackRowViewModel row) => new(row, this, () => IsTimelineTab = true);

    /// <summary>
    /// The panel goes with the highlighted track: while a track's properties are open, highlighting another track, or
    /// opening another timeline, shows that track's instead. The channel picker holds choices not applied yet, for its
    /// own track, so another track closes it.
    /// </summary>
    void PanelsFollowSelectedTrack()
    {
        if (ChannelPicker is not null && ChannelPicker.Track != SelectedTrack?.Track) ChannelPicker = null;
        ShowSelectedTrack();
    }

    /// <summary>
    /// The inspector is context aware: with no clip or handle picked (and the channel picker closed), it shows the
    /// highlighted track's properties.
    /// </summary>
    void ShowSelectedTrack()
    {
        if (ChannelPicker is not null || Properties is not null || PointProperties is not null || _selection.Count > 0 || _selectedPoints.Count > 0) return;
        if (TrackProperties?.Row == SelectedTrack) return;
        TrackProperties = SelectedTrack is { } row ? NewTrackProperties(row) : null;
    }

    partial void OnChannelPickerChanged(ChannelPickerViewModel? value)
    {
        if (value is null) ShowSelectedTrack();
    }

    /// <summary>
    /// Sets which channels a track controls, in the given order. Channels it keeps keep their envelopes; removed
    /// ones lose theirs (undo brings them back). A track getting its first channels is expanded, to show them.
    /// </summary>
    /// <param name="rename">A new name for the track, in the same edit (a new track named after its fixtures).</param>
    public void SetTrackChannels(TimelineTrack track, IReadOnlyList<LaneTarget> targets, string? rename = null)
    {
        var before = track.Channels.ToList();
        var after = targets.Distinct().Select(t => before.FirstOrDefault(l => l.Target == t) ?? new ChannelLane { Target = t }).ToList();
        if (before.SequenceEqual(after)) return;
        if (before.Count == 0 && after.Count > 0) _expanded.Add(track.Id);
        // A channel taken off the track takes its envelopes in every clip with it (undo brings them back).
        var kept = after.Select(l => l.Target).ToHashSet();
        var clips = track.Events.OfType<LightingClip>().ToList();
        Edit(rename is null ? "Change channels" : "Choose fixtures", () =>
        {
            track.Channels = after.ToList();
            if (rename is not null) track.Name = rename;
            foreach (var c in clips) c.Lanes = c.Lanes.Where(l => kept.Contains(l.Target)).ToList();
        }, roots: [Only(track), .. clips.Select(Only)]);
        if (Tracks.FirstOrDefault(t => t.Track == track) is { } row)
        {
            if (_expanded.Contains(track.Id)) row.IsExpanded = true;
            if (rename is not null) row.Refresh();
        }
    }

    /// <summary>Whether a track's moving heads move in black (see <see cref="TimelineTrack.MoveInBlack"/>).</summary>
    public void SetMoveInBlack(TimelineTrack track, bool move)
    {
        if (track.MoveInBlack == move) return;
        Edit(move ? "Move in black" : "Don't move in black", () => track.MoveInBlack = move, roots: Only(track));
    }

    /// <summary>VenueMagic's track option: draw each channel's held level between clips.</summary>
    public void SetDrawLevelsBetweenClips(TimelineTrack track, bool draw)
    {
        if (track.DrawLevelsBetweenClips == draw) return;
        Edit(draw ? "Draw levels between clips" : "Hide levels between clips", () => track.DrawLevelsBetweenClips = draw, roots: Only(track));
    }
}
