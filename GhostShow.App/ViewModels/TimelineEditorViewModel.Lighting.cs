using CommunityToolkit.Mvvm.ComponentModel;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Timeline;

namespace GhostShow.App.ViewModels;

/// <summary>
/// A row of the lanes area: a track's own row, or (for an expanded lighting track) one of its channels. The header
/// list and the canvas both lay out from these, so they always line up.
/// </summary>
public sealed record TimelineRow(int TrackIndex, TrackRowViewModel Track, ChannelLane? Lane, double Top, double Height)
{
    public double Bottom => Top + Height;
}

/// <summary>
/// Lighting tracks: the channels each track controls (picked with right-click → Channels…), and the blocks
/// (<see cref="LightingClip"/>) holding their levels as points. Blocks are events: moved, copied, deleted like audio;
/// their edges stretch them. Expanded, each channel has its own row where points are added (double-click: into the
/// block there, or a new block), dragged (time within the block, and level), switched between fade and step, and
/// deleted. Point times are shown and edited as timeline times; inside a block they're relative to its start.
/// </summary>
public sealed partial class TimelineEditorViewModel
{
    public const double TrackRowHeight = 46, LaneRowHeight = 40;

    readonly HashSet<Guid> _expanded = [];
    readonly HashSet<CurvePoint> _selectedPoints = [];

    /// <summary>Every row of the lanes area, top to bottom.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TimelineRow> Rows { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlySet<CurvePoint> SelectedPoints { get; private set; } = new HashSet<CurvePoint>();

    /// <summary>Exact values for the selected point, when exactly one point (and no event) is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPropertiesHint))]
    public partial PointPropertiesViewModel? PointProperties { get; private set; }

    /// <summary>The channel picker for a lighting track, while it's open (it takes the properties panel).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPropertiesHint))]
    public partial ChannelPickerViewModel? ChannelPicker { get; private set; }

    /// <summary>Nothing to show in the properties panel: show how to use the editor instead.</summary>
    public bool ShowPropertiesHint => Properties is null && PointProperties is null && ChannelPicker is null;

    public double ContentHeight => Rows.Count == 0 ? 0 : Rows[^1].Bottom;

    // ---- layout ----

    internal bool IsExpanded(TimelineTrack track) => _expanded.Contains(track.Id);

    internal void SetExpanded(TrackRowViewModel row, bool expanded)
    {
        if (expanded) _expanded.Add(row.Track.Id);
        else _expanded.Remove(row.Track.Id);
        Relayout();
    }

    public void Relayout()
    {
        var rows = new List<TimelineRow>();
        double top = 0;
        for (int i = 0; i < Tracks.Count; i++)
        {
            var track = Tracks[i];
            rows.Add(new TimelineRow(i, track, null, top, TrackRowHeight));
            top += TrackRowHeight;
            if (!track.IsExpanded) continue;
            foreach (var lane in track.Track.Channels)
            {
                rows.Add(new TimelineRow(i, track, lane, top, LaneRowHeight));
                top += LaneRowHeight;
            }
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

    public ChannelLane? LaneOf(CurvePoint point) => AllLanes().FirstOrDefault(l => l.Points.Contains(point));

    /// <summary>The block a lane belongs to (null for a lane directly on a track, from before blocks).</summary>
    public LightingClip? ClipOf(ChannelLane lane) =>
        (SelectedTimeline?.Tracks ?? []).SelectMany(t => t.Events.OfType<LightingClip>()).FirstOrDefault(c => c.Lanes.Contains(lane));

    /// <summary>A point's time on the timeline (its block's start plus its time in the block).</summary>
    public TimeSpan AbsoluteTime(CurvePoint point) =>
        LaneOf(point) is { } lane && ClipOf(lane) is { } clip ? clip.Start + point.Time : point.Time;

    /// <summary>The block on a track covering a time: the latest-starting one, as that's the one playing there.</summary>
    static LightingClip? ClipAt(TimelineTrack track, TimeSpan time) =>
        track.Events.OfType<LightingClip>().Where(c => c.Start <= time && time <= c.Start + c.Length).MaxBy(c => c.Start);

    /// <summary>The level a channel plays at a time on one track (from the latest block started by then), or null.</summary>
    static byte? PlayedValue(TimelineTrack track, LaneTarget target, TimeSpan time)
    {
        foreach (var clip in track.Events.OfType<LightingClip>().Where(c => c.Enabled && c.Start <= time).OrderByDescending(c => c.Start))
        {
            if (clip.LaneFor(target) is not { Points.Count: > 0 } lane) continue;
            var t = time - clip.Start;
            return lane.ValueAt(t < clip.Length ? t : clip.Length);
        }
        return null;
    }

    TimelineTrack? TrackOfChannel(ChannelLane trackLane) => SelectedTimeline?.Tracks.FirstOrDefault(t => t.Channels.Contains(trackLane));

    // ---- point selection (called by the canvas) ----

    /// <summary>Click on a point: select it alone, or with additive (Ctrl) toggle it in the selection.</summary>
    public void SelectPoint(CurvePoint point, bool additive)
    {
        if (!additive) _selectedPoints.Clear();
        _selection.Clear();
        if (additive && _selectedPoints.Contains(point)) _selectedPoints.Remove(point);
        else _selectedPoints.Add(point);
        SelectionChanged();
    }

    void PointSelectionChanged()
    {
        SelectedPoints = new HashSet<CurvePoint>(_selectedPoints);
        var single = _selection.Count == 0 && _selectedPoints.Count == 1 ? _selectedPoints.First() : null;
        if (PointProperties?.Point != single)
            PointProperties = single is null || LaneOf(single) is not { } lane ? null : new PointPropertiesViewModel(single, lane, this);
        else
            PointProperties?.Refresh();
    }

    // ---- point edits ----

    /// <summary>
    /// Double-click on a channel's row: a point at that time (snapped) and level, in the block there; where there's no
    /// block, a new block starting there holds it.
    /// </summary>
    /// <param name="trackLane">The channel (one of the track's <see cref="TimelineTrack.Channels"/>).</param>
    public CurvePoint AddPoint(ChannelLane trackLane, TimeSpan time, byte value)
    {
        time = SnapTime(time < TimeSpan.Zero ? TimeSpan.Zero : time);
        var track = TrackOfChannel(trackLane) ?? throw new InvalidOperationException("That channel isn't on a track of this timeline.");
        CurvePoint point;
        if (ClipAt(track, time) is { } clip)
        {
            var lane = clip.LaneFor(trackLane.Target);
            if (lane is null) clip.Lanes.Add(lane = new ChannelLane { Target = trackLane.Target });   // empty: plays nothing until it has points
            point = new CurvePoint { Time = time - clip.Start, Value = value };
            EditLanes("Add point", [lane], () => lane.Points.Add(point));
        }
        else
        {
            point = new CurvePoint { Time = TimeSpan.Zero, Value = value };
            var block = new LightingClip { Name = "Lighting", Start = time, Lanes = [new ChannelLane { Target = trackLane.Target, Points = [point] }] };
            History.Execute("Add lighting block", () => track.Events.Add(block), () => track.Events.Remove(block));
        }
        _selectedPoints.Clear();
        _selection.Clear();
        _selectedPoints.Add(point);
        SelectionChanged();
        return point;
    }

    /// <summary>
    /// "Add event" on a lighting track: a new block at the time, holding each of the track's channels at the level it
    /// plays there (so it changes nothing until edited).
    /// </summary>
    void AddBlockAt(TrackRowViewModel row, TimeSpan time)
    {
        var track = row.Track;
        if (track.Channels.Count == 0)
        {
            EditChannels(row);
            return;
        }
        var block = new LightingClip
        {
            Name = "Lighting", Start = time,
            Lanes = track.Channels.Select(c => new ChannelLane
            {
                Target = c.Target, Points = [new CurvePoint { Time = TimeSpan.Zero, Value = PlayedValue(track, c.Target, time) ?? 0 }],
            }).ToList(),
        };
        History.Execute("Add lighting block", () => track.Events.Add(block), () => track.Events.Remove(block));
        _selectedPoints.Clear();
        _selection.Clear();
        _selection.Add(block.Id);
        SelectionChanged();
    }

    /// <summary>
    /// Resizes a lighting block from either edge, stretching its lines to the new length (one undo step). Dragging the
    /// left edge keeps the end where it is.
    /// </summary>
    public void StretchClip(LightingClip clip, TimeSpan start, TimeSpan length)
    {
        start = start < TimeSpan.Zero ? TimeSpan.Zero : start;
        if (length < TimeSpan.FromMilliseconds(50) || (start == clip.Start && length == clip.Length)) return;
        var (oldStart, oldLength) = (clip.Start, clip.Length);
        var before = Snapshot(clip.Lanes);
        clip.Stretch(length);
        clip.Start = start;
        var after = Snapshot(clip.Lanes);
        History.Execute("Resize lighting block",
                        () => { Restore(after); (clip.Start, clip.Length) = (start, length); },
                        () => { Restore(before); (clip.Start, clip.Length) = (oldStart, oldLength); });
    }

    /// <summary>
    /// A drag of selected points: by delta time (each snapped on the timeline, and kept within its block) and delta
    /// level. One undo step.
    /// </summary>
    public void MoveSelectedPoints(TimeSpan deltaTime, int deltaValue)
    {
        var points = _selectedPoints.ToList();
        if (points.Count == 0 || (deltaTime == TimeSpan.Zero && deltaValue == 0)) return;
        var where = points.ToDictionary(p => p, p => LaneOf(p) is { } lane ? ClipOf(lane) : null);
        EditLanes(points.Count == 1 ? "Move point" : $"Move {points.Count} points", points.Select(LaneOf).OfType<ChannelLane>(), () =>
        {
            foreach (var p in points)
            {
                var clip = where[p];
                var offset = clip?.Start ?? TimeSpan.Zero;
                p.Time = ClampToClip(SnapTime(offset + p.Time + deltaTime) - offset, clip);
                p.Value = (byte)Math.Clamp(p.Value + deltaValue, 0, 255);
            }
        });
    }

    static TimeSpan ClampToClip(TimeSpan time, LightingClip? clip) =>
        time < TimeSpan.Zero ? TimeSpan.Zero : clip is not null && time > clip.Length ? clip.Length : time;

    void DeleteSelectedPoints()
    {
        var points = _selectedPoints.ToList();
        EditLanes(points.Count == 1 ? "Delete point" : $"Delete {points.Count} points", points.Select(LaneOf).OfType<ChannelLane>(),
                  () => { foreach (var lane in AllLanes()) lane.Points.RemoveAll(points.Contains); });
        _selectedPoints.Clear();
        SelectionChanged();
    }

    /// <summary>Fade into this point from the previous one, or hold and jump (step).</summary>
    public void SetStep(CurvePoint point, bool step)
    {
        if (point.Step == step || LaneOf(point) is not { } lane) return;
        EditLanes(step ? "Make point a step" : "Make point a fade", [lane], () => point.Step = step);
    }

    /// <summary>Exact values from the properties panel (time on the timeline); typing in one field merges into one undo step.</summary>
    internal void SetPoint(CurvePoint point, TimeSpan? time = null, byte? value = null, string field = "")
    {
        if (LaneOf(point) is not { } lane) return;
        var clip = ClipOf(lane);
        var newTime = time is { } t ? ClampToClip(t - (clip?.Start ?? TimeSpan.Zero), clip) : point.Time;
        var newValue = value ?? point.Value;
        if (newTime == point.Time && newValue == point.Value) return;
        EditLanes(time is not null ? "Move point" : "Change level", [lane], () => { point.Time = newTime; point.Value = newValue; }, (point, field));
    }

    /// <summary>
    /// Makes an edit to points undoable: records each affected lane's points (and their values) before and after,
    /// keeping points in time order. Undo and redo restore those exact states.
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

    static List<(ChannelLane Lane, (CurvePoint Point, TimeSpan Time, byte Value, bool Step)[] Points)> Snapshot(IEnumerable<ChannelLane> lanes) =>
        lanes.Select(l => (l, l.Points.Select(p => (p, p.Time, p.Value, p.Step)).ToArray())).ToList();

    static void Restore(List<(ChannelLane Lane, (CurvePoint Point, TimeSpan Time, byte Value, bool Step)[] Points)> state)
    {
        foreach (var (lane, points) in state)
        {
            foreach (var (point, time, value, step) in points)
            {
                point.Time = time;
                point.Value = value;
                point.Step = step;
            }
            lane.Points = points.Select(p => p.Point).ToList();
        }
    }

    // ---- channels ----

    /// <summary>Right-click → Channels…: opens the picker in the properties panel.</summary>
    public void EditChannels(TrackRowViewModel row)
    {
        if (!row.IsLighting) return;
        SelectedTrack = row;
        ChannelPicker = new ChannelPickerViewModel(row.Track, _session, this, targets =>
        {
            SetTrackChannels(row.Track, targets);
            ChannelPicker = null;
        }, () => ChannelPicker = null);
    }

    /// <summary>
    /// Sets which channels a track controls, in the given order. Channels it keeps keep their points; removed ones
    /// lose theirs (undo brings them back). A track getting its first channels is expanded, so points can be added.
    /// </summary>
    public void SetTrackChannels(TimelineTrack track, IReadOnlyList<LaneTarget> targets)
    {
        var before = track.Channels.ToList();
        var after = targets.Distinct().Select(t => before.FirstOrDefault(l => l.Target == t) ?? new ChannelLane { Target = t }).ToList();
        if (before.SequenceEqual(after)) return;
        if (before.Count == 0 && after.Count > 0) _expanded.Add(track.Id);
        // A channel taken off the track takes its lines in every block with it (undo brings them back).
        var kept = after.Select(l => l.Target).ToHashSet();
        var clips = track.Events.OfType<LightingClip>().ToList();
        var clipLanesBefore = clips.ToDictionary(c => c, c => c.Lanes.ToList());
        var clipLanesAfter = clips.ToDictionary(c => c, c => c.Lanes.Where(l => kept.Contains(l.Target)).ToList());
        History.Execute("Change channels",
                        () => { track.Channels = after.ToList(); foreach (var (c, lanes) in clipLanesAfter) c.Lanes = lanes.ToList(); },
                        () => { track.Channels = before.ToList(); foreach (var (c, lanes) in clipLanesBefore) c.Lanes = lanes.ToList(); });
        if (Tracks.FirstOrDefault(t => t.Track == track) is { } row && _expanded.Contains(track.Id)) row.IsExpanded = true;
    }
}
