using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Timeline;

namespace GhostShow.App.ViewModels;

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
    public const double CollapsedTrackHeight = 34, CollapsedLightingHeight = 64, ExpandedTrackHeight = 150, AudioTrackHeight = 64;

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
        return _levels[track] = TrackLevels.ResolveAll(track, target => TimelineCompiler.FunctionOf(target, patched));
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
        if (clip.Locked || clip.Effect is not null) return null;   // an effect's envelopes are made from its settings
        var at = SnapTime(time) - clip.Start;
        if (at <= TimeSpan.Zero || at >= clip.Length) at = time - clip.Start;   // the grid would put it on an edge
        if (at <= TimeSpan.Zero || at >= clip.Length) return null;
        byte start = StartLevels(clip)(target);
        var lane = clip.LaneFor(target);
        var point = new CurvePoint { Time = at, Value = Envelope.Round(Envelope.ValueAt(clip.EnvelopeFor(target, start), at)) };
        if (lane is null)
        {
            lane = new ChannelLane { Target = target };
            var newLane = lane;
            var end = new CurvePoint { Time = clip.Length, Value = start };
            History.Execute("Add envelope handle",
                            () => { newLane.Points = [point, end]; clip.Lanes.Add(newLane); },
                            () => clip.Lanes.Remove(newLane));
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
        History.Execute("Add Level Control clip", () => track.Events.Add(clip), () => track.Events.Remove(clip));
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
        History.Execute($"Add {info.Name} effect", () => track.Events.Add(clip), () => track.Events.Remove(clip));
        _selectedPoints.Clear();
        _selection.Clear();
        _selection.Add(clip.Id);
        SelectionChanged();
        return clip;
    }

    /// <summary>The toolbar's Effects menu: the effect at the playhead, on the selected lighting track (or the first one).</summary>
    [RelayCommand]
    void AddEffect(EffectKind kind)
    {
        if (EffectsTrack() is { } row) AddEffectAt(row, SnapTime(_playback.Position), kind);
    }

    /// <summary>The Effects menu's Level Control: a clip at the playhead, on the selected lighting track (or the first one).</summary>
    [RelayCommand]
    void AddLevelControl()
    {
        if (EffectsTrack() is { } row) AddClipAt(row, SnapTime(_playback.Position));
    }

    TrackRowViewModel? EffectsTrack() => SelectedTrack is { IsLighting: true } selected ? selected : Tracks.FirstOrDefault(t => t.IsLighting);

    /// <summary>Changes an effect clip's settings (one undoable edit; typing into one field merges).</summary>
    public void ChangeEffect(LightingClip clip, string description, Action<LightingEffect> change, string field)
    {
        if (clip.Effect is not { } current || clip.Locked) return;
        var changed = current.Clone();
        change(changed);
        if (EffectEquals(current, changed)) return;
        History.Execute(description, () => clip.Effect = changed, () => clip.Effect = current, (clip.Id, "effect:" + field));
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
        var oldLanes = clip.Lanes;
        History.Execute("Convert to Level Control",
                        () => { clip.Effect = null; clip.Lanes = lanes; },
                        () => { clip.Effect = effect; clip.Lanes = oldLanes; });
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
        var (oldStart, oldLength) = (clip.Start, clip.Length);
        var before = Snapshot(clip.Lanes);
        if (start != clip.Start) clip.ResizeStart(start);
        if (clip.Length != length) clip.ResizeEnd(length, startLevels);
        var (newStart, newLength) = (clip.Start, clip.Length);
        var after = Snapshot(clip.Lanes);
        History.Execute(clip.ScaleOnResize ? "Resize clip" : "Crop clip",
                        () => { Restore(after); (clip.Start, clip.Length) = (newStart, newLength); },
                        () => { Restore(before); (clip.Start, clip.Length) = (oldStart, oldLength); });
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
        var old = channel.StartLevel;
        History.Execute("Change start level", () => channel.StartLevel = level, () => channel.StartLevel = old, (channel, field));
    }

    public void SetChannelStart(ChannelLane channel, ChannelStart start)
    {
        if (channel.Start == start) return;
        var old = channel.Start;
        string description = start switch
        {
            ChannelStart.Fixed => "Start at a fixed level",
            ChannelStart.LeaveAlone => "Leave alone until the first clip",
            _ => "Start at the first clip",
        };
        History.Execute(description, () => channel.Start = start, () => channel.Start = old);
    }

    /// <summary>
    /// Makes an edit to handles undoable: records each affected envelope's handles (and their values) before and
    /// after, keeping them in time order. Undo and redo restore those exact states.
    /// </summary>
    void EditLanes(string description, IEnumerable<ChannelLane> lanes, Action change, object? mergeKey = null)
    {
        var affected = lanes.Distinct().ToList();
        var before = Snapshot(affected);
        change();
        foreach (var lane in affected) lane.Sort();
        var after = Snapshot(affected);
        History.Execute(description, () => Restore(after), () => Restore(before), mergeKey);
    }

    static List<(ChannelLane Lane, (CurvePoint Point, TimeSpan Time, byte Value, CurveKind Curve, Guid? Group)[] Points)> Snapshot(IEnumerable<ChannelLane> lanes) =>
        lanes.Select(l => (l, l.Points.Select(p => (p, p.Time, p.Value, p.Curve, p.Group)).ToArray())).ToList();

    static void Restore(List<(ChannelLane Lane, (CurvePoint Point, TimeSpan Time, byte Value, CurveKind Curve, Guid? Group)[] Points)> state)
    {
        foreach (var (lane, points) in state)
        {
            foreach (var (point, time, value, curve, group) in points)
            {
                point.Time = time;
                point.Value = value;
                point.Curve = curve;
                point.Group = group;
            }
            lane.Points = points.Select(p => p.Point).ToList();
        }
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
        var splits = candidates.Where(e => !e.Locked && e is LightingClip { Effect: null } or AudioEvent && e.Start < at && at < e.Start + LengthOf(e))
                               .Select(e => (Track: TrackContaining(e)!, Event: e)).ToList();
        if (splits.Count == 0) return;
        var undo = new List<Action>();
        var redo = new List<Action>();
        var added = new List<TimelineEvent>();
        foreach (var (track, e) in splits)
        {
            switch (e)
            {
                case LightingClip clip:
                {
                    // Split to see the result, then put it back: the history applies it.
                    var before = (Lanes: Snapshot(clip.Lanes), clip.Length);
                    var second = clip.SplitAt(at - clip.Start, StartLevels(clip))!;
                    var after = (Lanes: Snapshot(clip.Lanes), clip.Length);
                    Restore(before.Lanes);
                    clip.Length = before.Length;
                    redo.Add(() => { Restore(after.Lanes); clip.Length = after.Length; track.Events.Add(second); });
                    undo.Add(() => { track.Events.Remove(second); Restore(before.Lanes); clip.Length = before.Length; });
                    added.Add(second);
                    break;
                }
                case AudioEvent sound:
                {
                    var cut = at - sound.Start;
                    var second = (AudioEvent)sound.Clone();
                    (second.Start, second.Offset, second.Length, second.FadeIn) = (at, sound.Offset + cut, sound.Length - cut, TimeSpan.Zero);
                    var (oldLength, oldFadeOut) = (sound.Length, sound.FadeOut);
                    redo.Add(() => { (sound.Length, sound.FadeOut) = (cut, TimeSpan.Zero); track.Events.Add(second); });
                    undo.Add(() => { track.Events.Remove(second); (sound.Length, sound.FadeOut) = (oldLength, oldFadeOut); });
                    added.Add(second);
                    break;
                }
            }
        }
        History.Execute(splits.Count == 1 ? "Split clip" : $"Split {splits.Count} clips",
                        () => { foreach (var r in redo) r(); },
                        () => { foreach (var u in Enumerable.Reverse(undo)) u(); });
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
        var before = events.ToDictionary(e => e, e => e.Locked);
        History.Execute(lockThem ? (events.Count == 1 ? "Lock clip" : $"Lock {events.Count} clips") : (events.Count == 1 ? "Unlock clip" : $"Unlock {events.Count} clips"),
                        () => { foreach (var e in events) e.Locked = lockThem; },
                        () => { foreach (var (e, locked) in before) e.Locked = locked; });
    }

    public bool CanLink => _selection.Count > 1;
    public bool CanUnlink => SelectedEvents().Any(e => e.LinkGroup is not null);

    /// <summary>Links the selected clips: from now on, moving one moves them all.</summary>
    public void Link()
    {
        var events = SelectedEvents().ToList();
        if (events.Count < 2) return;
        var group = Guid.NewGuid();
        var before = events.ToDictionary(e => e, e => e.LinkGroup);
        History.Execute($"Link {events.Count} clips", () => { foreach (var e in events) e.LinkGroup = group; },
                        () => { foreach (var (e, g) in before) e.LinkGroup = g; });
    }

    /// <summary>Unlinks the selected clips from whatever they were linked to.</summary>
    public void Unlink()
    {
        var events = SelectedEvents().Where(e => e.LinkGroup is not null).ToList();
        if (events.Count == 0) return;
        var before = events.ToDictionary(e => e, e => e.LinkGroup);
        History.Execute(events.Count == 1 ? "Unlink clip" : $"Unlink {events.Count} clips", () => { foreach (var e in events) e.LinkGroup = null; },
                        () => { foreach (var (e, g) in before) e.LinkGroup = g; });
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
        ChannelPicker = new ChannelPickerViewModel(row.Track, _session, this, ShowFixtures, targets =>
        {
            SetTrackChannels(row.Track, targets);
            ChannelPicker = null;
        }, () => ChannelPicker = null);
    }

    /// <summary>Right-click → Properties…: the track's name, options and (lighting) each channel's start level.</summary>
    public void EditTrackProperties(TrackRowViewModel row)
    {
        SelectedTrack = row;
        ChannelPicker = null;
        ClearSelection();   // the panel shows one thing: now the track
        TrackProperties = new TrackPropertiesViewModel(row, this, () => TrackProperties = null);
    }

    /// <summary>
    /// Sets which channels a track controls, in the given order. Channels it keeps keep their envelopes; removed
    /// ones lose theirs (undo brings them back). A track getting its first channels is expanded, to show them.
    /// </summary>
    public void SetTrackChannels(TimelineTrack track, IReadOnlyList<LaneTarget> targets)
    {
        var before = track.Channels.ToList();
        var after = targets.Distinct().Select(t => before.FirstOrDefault(l => l.Target == t) ?? new ChannelLane { Target = t }).ToList();
        if (before.SequenceEqual(after)) return;
        if (before.Count == 0 && after.Count > 0) _expanded.Add(track.Id);
        // A channel taken off the track takes its envelopes in every clip with it (undo brings them back).
        var kept = after.Select(l => l.Target).ToHashSet();
        var clips = track.Events.OfType<LightingClip>().ToList();
        var clipLanesBefore = clips.ToDictionary(c => c, c => c.Lanes.ToList());
        var clipLanesAfter = clips.ToDictionary(c => c, c => c.Lanes.Where(l => kept.Contains(l.Target)).ToList());
        History.Execute("Change channels",
                        () => { track.Channels = after.ToList(); foreach (var (c, lanes) in clipLanesAfter) c.Lanes = lanes.ToList(); },
                        () => { track.Channels = before.ToList(); foreach (var (c, lanes) in clipLanesBefore) c.Lanes = lanes.ToList(); });
        if (Tracks.FirstOrDefault(t => t.Track == track) is { } row && _expanded.Contains(track.Id)) row.IsExpanded = true;
    }

    /// <summary>VenueMagic's track option: draw each channel's held level between clips.</summary>
    public void SetDrawLevelsBetweenClips(TimelineTrack track, bool draw)
    {
        if (track.DrawLevelsBetweenClips == draw) return;
        History.Execute(draw ? "Draw levels between clips" : "Hide levels between clips",
                        () => track.DrawLevelsBetweenClips = draw, () => track.DrawLevelsBetweenClips = !draw);
    }
}
