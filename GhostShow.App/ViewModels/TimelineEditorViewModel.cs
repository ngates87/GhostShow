using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.App.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Timeline;

namespace GhostShow.App.ViewModels;

/// <summary>An open timeline in the editor's tab strip.</summary>
public sealed partial class TimelineTabViewModel(ShowTimeline timeline) : ObservableObject
{
    public ShowTimeline Timeline { get; } = timeline;
    public string Title => Timeline.HasUnsavedChanges ? $"{Timeline.Name} *" : Timeline.Name;
    public string ToolTip => Timeline.TreePath;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ToolTip));
    }
}

/// <summary>One channel of a lighting track, as listed in the track's header when it's expanded.</summary>
public sealed record LaneRowViewModel(ChannelLane Lane, string Label, string Color);

/// <summary>
/// A track header: name, kind, mute; for a lighting track also its channels (right-click → Channels…) and whether
/// it's expanded to one row per channel. Edits go through the history like everything else.
/// </summary>
public sealed partial class TrackRowViewModel : ObservableObject
{
    readonly TimelineEditorViewModel _editor;

    public TrackRowViewModel(TimelineTrack track, TimelineEditorViewModel editor)
    {
        Track = track;
        _editor = editor;
        IsExpanded = editor.IsExpanded(track);
        RefreshLanes();
    }

    TimelineEditorViewModel editor => _editor;

    public TimelineTrack Track { get; }

    public bool IsLighting => Track.Kind == TrackKind.Lighting;

    /// <summary>Lighting: one row per channel under the track's own row (where points are edited).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Height), nameof(ExpandGlyph))]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value) => _editor.SetExpanded(this, value);

    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    public ObservableCollection<LaneRowViewModel> Lanes { get; } = new();

    /// <summary>The header's height; the canvas draws the track's rows at the same heights.</summary>
    public double Height => TimelineEditorViewModel.TrackRowHeight + (IsExpanded ? Lanes.Count * TimelineEditorViewModel.LaneRowHeight : 0);

    public string ChannelSummary => Lanes.Count switch { 0 => "No channels: right-click → Channels…", 1 => "1 channel", var n => $"{n} channels" };

    [RelayCommand]
    void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    void EditChannels() => _editor.EditChannels(this);

    public TrackKind Kind => Track.Kind;

    public string KindLabel => Track.Kind switch { TrackKind.Lighting => "Lighting", TrackKind.Audio => "Audio", _ => "Markers" };

    public string Name
    {
        get => Track.Name;
        set
        {
            if (value == Track.Name) return;
            string old = Track.Name;
            editor.History.Execute("Rename track", () => Track.Name = value, () => Track.Name = old, (Track.Id, "name"));
        }
    }

    public bool Enabled
    {
        get => Track.Enabled;
        set
        {
            if (value == Track.Enabled) return;
            editor.History.Execute(value ? "Unmute track" : "Mute track", () => Track.Enabled = value, () => Track.Enabled = !value);
        }
    }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Enabled));
        RefreshLanes();
    }

    void RefreshLanes()
    {
        var lanes = Track.Channels;
        if (Lanes.Select(l => l.Lane).SequenceEqual(lanes) && Lanes.All(l => l.Label == _editor.LaneLabel(l.Lane.Target))) return;
        Lanes.Clear();
        foreach (var lane in lanes) Lanes.Add(new LaneRowViewModel(lane, _editor.LaneLabel(lane.Target), TimelineEditorViewModel.LaneColor(lane)));
        OnPropertyChanged(nameof(Height));
        OnPropertyChanged(nameof(ChannelSummary));
    }
}

/// <summary>
/// The timeline editor: the show's timeline tree, the open timelines (tabs), the one being edited (and played), its
/// tracks, the selected events, and every editing command. All changes go through the timeline's own
/// <see cref="History"/> (undoable, kept while its tab is open) and then recompile the playing timeline, so edits
/// during playback take effect on the next playback update (see docs/timeline.md, "Editing while playing").
/// </summary>
public sealed partial class TimelineEditorViewModel : ObservableObject
{
    public static readonly IReadOnlyList<SnapOption> SnapOptions =
    [
        new("Off", TimeSpan.Zero), new("1/20 s", TimeSpan.FromSeconds(0.05)), new("1/10 s", TimeSpan.FromSeconds(0.1)),
        new("1/4 s", TimeSpan.FromSeconds(0.25)), new("1/2 s", TimeSpan.FromSeconds(0.5)), new("1 s", TimeSpan.FromSeconds(1)),
    ];
    const double MinZoom = 2, MaxZoom = 2000;   // pixels per second

    readonly ShowSession _session;
    readonly PlaybackViewModel _playback;
    readonly Func<Task<string?>> _pickAudioFile;
    readonly Action _markDirty;
    readonly HashSet<Guid> _selection = [];
    List<(int Track, TimelineEvent Event)> _clipboard = [];

    public TimelineEditorViewModel(ShowSession session, PlaybackViewModel playback, Func<Task<string?>> pickAudioFile, Action markDirty)
    {
        _session = session;
        _playback = playback;
        _pickAudioFile = pickAudioFile;
        _markDirty = markDirty;
        Tree = new TimelineTreeViewModel(session, markDirty, OpenTimeline, OnTimelineRemoved);
        Tree.StructureChanged += RefreshTitles;
        // Fixture names and channels in track headers follow patch changes (a renamed or removed fixture).
        session.Applied += () =>
        {
            foreach (var row in Tracks) row.Refresh();
            Relayout();
        };
        playback.Compiled += () => ErrorEvents = playback.Issues.Where(i => i.EventId is not null).Select(i => i.EventId!.Value).ToHashSet();
        Reload();
    }

    readonly Dictionary<Guid, EditHistory> _histories = new();

    /// <summary>The undo history of the timeline being edited; each open timeline keeps its own.</summary>
    [ObservableProperty]
    public partial EditHistory History { get; private set; } = new();

    public PlaybackViewModel Playback => _playback;

    public TimelineTreeViewModel Tree { get; }

    /// <summary>Open timelines, as tabs. The selected one is edited, and is the one the transport plays.</summary>
    public ObservableCollection<TimelineTabViewModel> Tabs { get; } = new();

    [ObservableProperty]
    public partial TimelineTabViewModel? SelectedTab { get; set; }

    public ObservableCollection<TrackRowViewModel> Tracks { get; } = new();

    [ObservableProperty]
    public partial ShowTimeline? SelectedTimeline { get; set; }

    [ObservableProperty]
    public partial TrackRowViewModel? SelectedTrack { get; set; }

    /// <summary>Property editor for the selected event, when exactly one is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPropertiesHint))]
    public partial EventPropertiesViewModel? Properties { get; private set; }

    [ObservableProperty]
    public partial double PixelsPerSecond { get; set; } = 60;

    [ObservableProperty]
    public partial SnapOption Snap { get; set; } = SnapOptions[2];

    /// <summary>Bumped whenever the timeline's content changes, so the canvas redraws.</summary>
    [ObservableProperty]
    public partial long Revision { get; private set; }

    [ObservableProperty]
    public partial IReadOnlySet<Guid> Selection { get; private set; } = new HashSet<Guid>();

    [ObservableProperty]
    public partial IReadOnlySet<Guid> ErrorEvents { get; private set; } = new HashSet<Guid>();

    public string UndoLabel => History.UndoDescription is { } d ? $"Undo {d}" : "Undo";
    public string RedoLabel => History.RedoDescription is { } d ? $"Redo {d}" : "Redo";
    public string SelectionLabel => (_selection.Count, _selectedPoints.Count) switch
    {
        (0, 0) => "Nothing selected",
        (0, 1) => "1 point selected",
        (0, var p) => $"{p} points selected",
        (1, _) => "1 event selected",
        (var n, _) => $"{n} events selected",
    };

    /// <summary>Renaming is done in the tree (it renames the file too).</summary>
    public string TimelineName => SelectedTimeline?.Name ?? "";

    public bool Loop
    {
        get => SelectedTimeline?.Loop ?? false;
        set
        {
            if (SelectedTimeline is not { } timeline || value == timeline.Loop) return;
            History.Execute(value ? "Loop timeline" : "Stop looping", () => timeline.Loop = value, () => timeline.Loop = !value);
        }
    }

    /// <summary>Explicit length in seconds; 0 means "until the last event ends".</summary>
    public decimal LengthSeconds
    {
        get => (decimal)(SelectedTimeline?.Length?.TotalSeconds ?? 0);
        set
        {
            if (SelectedTimeline is not { } timeline) return;
            TimeSpan? length = value <= 0 ? null : TimeSpan.FromSeconds((double)value);
            if (length == timeline.Length) return;
            var old = timeline.Length;
            History.Execute("Change timeline length", () => timeline.Length = length, () => timeline.Length = old, (timeline.Id, "length"));
        }
    }

    partial void OnSelectedTabChanged(TimelineTabViewModel? value)
    {
        if (value?.Timeline != SelectedTimeline) SelectedTimeline = value?.Timeline;
    }

    partial void OnSelectedTimelineChanged(ShowTimeline? value)
    {
        if (SelectedTab?.Timeline != value) SelectedTab = value is null ? null : Tabs.FirstOrDefault(t => t.Timeline == value) ?? AddTab(value);
        History = value is null ? new EditHistory() : HistoryOf(value);
        ClearSelection();
        RebuildTracks();
        _playback.SetTimeline(value);
        OnPropertyChanged(nameof(TimelineName));
        OnPropertyChanged(nameof(Loop));
        OnPropertyChanged(nameof(LengthSeconds));
        OnPropertyChanged(nameof(UndoLabel));
        OnPropertyChanged(nameof(RedoLabel));
        Revision++;
    }

    partial void OnSelectedTrackChanged(TrackRowViewModel? oldValue, TrackRowViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    partial void OnPixelsPerSecondChanged(double value) => Revision++;

    /// <summary>Rebuilds everything from the show, e.g. after New or Open: the tree, and the first timeline opened.</summary>
    public void Reload()
    {
        Tabs.Clear();
        _histories.Clear();
        SelectedTab = null;
        Tree.Rebuild();
        var first = Tree.AllNodes().OfType<TimelineNode>().FirstOrDefault()?.Timeline;
        SelectedTimeline = first;
        if (first is null) OnSelectedTimelineChanged(null);
        else Tree.Reveal(first);
    }

    /// <summary>Opens a timeline in a tab (or switches to its tab).</summary>
    public void OpenTimeline(ShowTimeline timeline) => SelectedTimeline = timeline;

    [RelayCommand]
    void CloseTab(TimelineTabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab is null) return;
        int index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        _histories.Remove(tab.Timeline.Id);
        if (SelectedTab == tab || SelectedTab is null)
            SelectedTimeline = Tabs.Count == 0 ? null : Tabs[Math.Clamp(index, 0, Tabs.Count - 1)].Timeline;
    }

    void OnTimelineRemoved(ShowTimeline timeline)
    {
        if (Tabs.FirstOrDefault(t => t.Timeline == timeline) is { } tab) CloseTab(tab);
    }

    /// <summary>After saving (the " *" marks go) or a rename in the tree.</summary>
    public void RefreshTitles()
    {
        foreach (var tab in Tabs) tab.Refresh();
        Tree.RefreshTitles();
        OnPropertyChanged(nameof(TimelineName));
    }

    TimelineTabViewModel AddTab(ShowTimeline timeline)
    {
        var tab = new TimelineTabViewModel(timeline);
        Tabs.Add(tab);
        return tab;
    }

    EditHistory HistoryOf(ShowTimeline timeline)
    {
        if (_histories.TryGetValue(timeline.Id, out var history)) return history;
        history = new EditHistory();
        history.Changed += OnEdited;
        return _histories[timeline.Id] = history;
    }

    // ---- selection (called by the canvas) ----

    /// <summary>Click on an event: select it alone, or with additive (Ctrl) toggle it in the selection.</summary>
    public void SelectEvent(Guid id, bool additive)
    {
        if (!additive) _selection.Clear();
        _selectedPoints.Clear();
        if (additive && _selection.Contains(id)) _selection.Remove(id);
        else _selection.Add(id);
        if (FindTrackOf(id) is { } trackIndex) SelectedTrack = Tracks[trackIndex];
        SelectionChanged();
    }

    public void ClearSelection()
    {
        _selection.Clear();
        _selectedPoints.Clear();
        SelectionChanged();
    }

    [RelayCommand]
    void SelectAll()
    {
        foreach (var e in AllEvents()) _selection.Add(e.Id);
        SelectionChanged();
    }

    // ---- edits from the canvas ----

    /// <summary>Moves every selected event by delta (snapped; no event goes before 0). One undo step.</summary>
    public void MoveSelection(TimeSpan delta)
    {
        var events = SelectedEvents().ToList();
        if (events.Count == 0 || delta == TimeSpan.Zero) return;
        var earliest = events.Min(e => e.Start);
        if (earliest + delta < TimeSpan.Zero) delta = -earliest;
        var before = events.ToDictionary(e => e, e => e.Start);
        var after = events.ToDictionary(e => e, e => SnapTime(e.Start + delta));
        History.Execute(events.Count == 1 ? "Move event" : $"Move {events.Count} events",
                        () => { foreach (var (e, t) in after) e.Start = t; },
                        () => { foreach (var (e, t) in before) e.Start = t; });
    }

    /// <summary>Drag of a block's right edge: an audio event's length, or a lighting block's (stretching its lines).</summary>
    public void ResizeEvent(Guid id, TimeSpan length)
    {
        switch (FindEvent(id))
        {
            case AudioEvent audio:
                length = SnapTime(audio.Start + (length < TimeSpan.Zero ? TimeSpan.Zero : length)) - audio.Start;
                if (audio.Length == length || length <= TimeSpan.Zero) return;
                var oldLength = audio.Length;
                History.Execute("Change audio length", () => audio.Length = length, () => audio.Length = oldLength);
                break;
            case LightingClip clip:
                StretchClip(clip, clip.Start, SnapTime(clip.Start + length) - clip.Start);
                break;
        }
    }

    /// <summary>
    /// Drag of a block's left edge to a new start (snapped): a lighting block stretches with its end kept; an audio
    /// block is trimmed, starting later (or earlier) into its file, its end kept.
    /// </summary>
    public void ResizeEventStart(Guid id, TimeSpan start)
    {
        start = SnapTime(start < TimeSpan.Zero ? TimeSpan.Zero : start);
        switch (FindEvent(id))
        {
            case LightingClip clip:
                var end = clip.Start + clip.Length;
                if (start < end) StretchClip(clip, start, end - start);
                break;
            case AudioEvent audio:
                var delta = start - audio.Start;
                if (audio.Offset + delta < TimeSpan.Zero) delta = -audio.Offset;   // can't start before the file does
                var visibleLength = LengthOf(audio);
                if (delta == TimeSpan.Zero || (visibleLength > TimeSpan.Zero && delta >= visibleLength)) return;
                var (oldStart, oldOffset, oldLen) = (audio.Start, audio.Offset, audio.Length);
                var (newStart, newOffset) = (audio.Start + delta, audio.Offset + delta);
                TimeSpan? newLen = audio.Length is { } l ? l - delta : null;
                History.Execute("Trim audio start",
                                () => (audio.Start, audio.Offset, audio.Length) = (newStart, newOffset, newLen),
                                () => (audio.Start, audio.Offset, audio.Length) = (oldStart, oldOffset, oldLen));
                break;
        }
    }

    /// <summary>Double-click on a lane: add an event of that track's kind there.</summary>
    public async Task AddEventAtAsync(int trackIndex, TimeSpan time)
    {
        if (trackIndex < 0 || trackIndex >= Tracks.Count) return;
        SelectedTrack = Tracks[trackIndex];
        await AddEventToSelectedTrackAsync(SnapTime(time));
    }

    public void SeekTo(TimeSpan time) => _playback.Seek(SnapTime(time < TimeSpan.Zero ? TimeSpan.Zero : time));

    // ---- commands ----

    /// <summary>Adds an event of the selected track's kind at the playhead (lighting: a point on each of its channels).</summary>
    [RelayCommand]
    Task AddEvent() => AddEventToSelectedTrackAsync(SnapTime(_playback.Position));

    async Task AddEventToSelectedTrackAsync(TimeSpan at)
    {
        if (SelectedTrack is not { } row) return;
        if (row.IsLighting)
        {
            AddBlockAt(row, at);
            return;
        }
        TimelineEvent? e = row.Kind switch
        {
            TrackKind.Audio => await NewAudioEventAsync(at),
            _ => new MarkerEvent { Name = $"Marker {row.Track.Events.Count + 1}", Start = at },
        };
        if (e is null) return;
        var track = row.Track;
        History.Execute($"Add {Describe(e)}", () => track.Events.Add(e), () => track.Events.Remove(e));
        _selection.Clear();
        _selection.Add(e.Id);
        SelectionChanged();
    }

    async Task<AudioEvent?> NewAudioEventAsync(TimeSpan at)
    {
        var path = await _pickAudioFile();
        if (path is null) return null;
        return new AudioEvent { Start = at, File = AudioPaths.ToReference(path, _session.ShowFolder) };
    }

    /// <summary>Deletes the selected events, or the selected points.</summary>
    [RelayCommand]
    void Delete()
    {
        if (_selectedPoints.Count > 0)
        {
            DeleteSelectedPoints();
            return;
        }
        var removals = SelectedEvents().Select(e => (Track: TrackContaining(e)!, Event: e, Index: TrackContaining(e)!.Events.IndexOf(e))).ToList();
        if (removals.Count == 0) return;
        History.Execute(removals.Count == 1 ? $"Delete {Describe(removals[0].Event)}" : $"Delete {removals.Count} events",
            () => { foreach (var r in removals) r.Track.Events.Remove(r.Event); },
            () => { foreach (var r in removals.OrderBy(r => r.Index)) r.Track.Events.Insert(Math.Min(r.Index, r.Track.Events.Count), r.Event); });
        ClearSelection();
    }

    /// <summary>Copies of the selected events, placed right after the last of them, on the same tracks.</summary>
    [RelayCommand]
    void Duplicate()
    {
        var events = SelectedEvents().ToList();
        if (events.Count == 0) return;
        var span = events.Max(EndOf) - events.Min(e => e.Start);
        var offset = span > TimeSpan.Zero ? span : TimeSpan.FromSeconds(1);
        PasteCopies(events.Select(e => (TrackContaining(e)!, e)).ToList(), e => e.Start + offset, "Duplicate");
    }

    [RelayCommand]
    void Copy()
    {
        _clipboard = SelectedEvents().Select(e => (Tracks.IndexOf(Tracks.First(t => t.Track.Events.Contains(e))), e.Clone())).ToList();
    }

    /// <summary>Pastes the copied events at the playhead, keeping their spacing; onto their own tracks when the kind matches.</summary>
    [RelayCommand]
    void Paste()
    {
        if (_clipboard.Count == 0 || Tracks.Count == 0) return;
        var earliest = _clipboard.Min(c => c.Event.Start);
        var at = SnapTime(_playback.Position);
        var targets = _clipboard.Select(c =>
        {
            var kind = KindOf(c.Event);
            var track = c.Track < Tracks.Count && Tracks[c.Track].Kind == kind ? Tracks[c.Track].Track
                      : SelectedTrack?.Kind == kind ? SelectedTrack.Track
                      : Tracks.FirstOrDefault(t => t.Kind == kind)?.Track;
            return (Track: track, c.Event);
        }).Where(t => t.Track is not null).Select(t => (t.Track!, t.Event)).ToList();
        PasteCopies(targets, e => at + (e.Start - earliest), "Paste");
    }

    void PasteCopies(List<(TimelineTrack Track, TimelineEvent Source)> sources, Func<TimelineEvent, TimeSpan> startOf, string verb)
    {
        var copies = sources.Select(s =>
        {
            var copy = s.Source.Clone();
            copy.Start = startOf(s.Source);
            return (s.Track, Copy: copy);
        }).ToList();
        if (copies.Count == 0) return;
        History.Execute(copies.Count == 1 ? $"{verb} {Describe(copies[0].Copy)}" : $"{verb} {copies.Count} events",
                        () => { foreach (var c in copies) c.Track.Events.Add(c.Copy); },
                        () => { foreach (var c in copies) c.Track.Events.Remove(c.Copy); });
        _selection.Clear();
        foreach (var c in copies) _selection.Add(c.Copy.Id);
        SelectionChanged();
    }

    [RelayCommand]
    void Undo() => History.Undo();

    [RelayCommand]
    void Redo() => History.Redo();

    [RelayCommand]
    void ZoomIn() => PixelsPerSecond = Math.Min(MaxZoom, PixelsPerSecond * 1.5);

    [RelayCommand]
    void ZoomOut() => PixelsPerSecond = Math.Max(MinZoom, PixelsPerSecond / 1.5);

    /// <summary>Fits the whole timeline into the given width (the view passes its visible width).</summary>
    public void ZoomToFit(double width)
    {
        double seconds = Math.Max(1, ContentLength().TotalSeconds * 1.05);
        PixelsPerSecond = Math.Clamp(width / seconds, MinZoom, MaxZoom);
    }

    [RelayCommand]
    void AddTrack(TrackKind kind)
    {
        if (SelectedTimeline is not { } timeline) return;
        int count = timeline.Tracks.Count(t => t.Kind == kind) + 1;
        var track = new TimelineTrack { Kind = kind, Name = kind switch { TrackKind.Lighting => $"Lighting {count}", TrackKind.Audio => $"Audio {count}", _ => $"Markers {count}" } };
        History.Execute("Add track", () => timeline.Tracks.Add(track), () => timeline.Tracks.Remove(track));
        SelectedTrack = Tracks.FirstOrDefault(t => t.Track == track);
    }

    [RelayCommand]
    void RemoveTrack()
    {
        if (SelectedTimeline is not { } timeline || SelectedTrack is not { } row) return;
        var track = row.Track;
        int index = timeline.Tracks.IndexOf(track);
        History.Execute($"Remove track '{track.Name}'", () => timeline.Tracks.Remove(track), () => timeline.Tracks.Insert(index, track));
    }

    /// <summary>Track order decides ties: where two events start together on one channel, the lower track wins.</summary>
    [RelayCommand]
    void MoveTrackUp() => MoveTrack(-1);

    [RelayCommand]
    void MoveTrackDown() => MoveTrack(+1);

    void MoveTrack(int direction)
    {
        if (SelectedTimeline is not { } timeline || SelectedTrack is not { } row) return;
        var track = row.Track;
        int from = timeline.Tracks.IndexOf(track), to = from + direction;
        if (to < 0 || to >= timeline.Tracks.Count) return;
        History.Execute("Move track",
                        () => { timeline.Tracks.RemoveAt(from); timeline.Tracks.Insert(to, track); },
                        () => { timeline.Tracks.RemoveAt(to); timeline.Tracks.Insert(from, track); });
    }

    // ---- helpers ----

    void OnEdited()
    {
        _markDirty();
        if (SelectedTimeline is { } edited)
        {
            edited.HasUnsavedChanges = true;
            foreach (var tab in Tabs) tab.Refresh();
            Tree.NodeOf(edited)?.Refresh();
        }
        RebuildTracksIfChanged();
        foreach (var row in Tracks) row.Refresh();
        _selection.RemoveWhere(id => FindEvent(id) is null);
        var points = AllLanes().SelectMany(l => l.Points).ToHashSet();
        _selectedPoints.RemoveWhere(p => !points.Contains(p));
        SelectionChanged();
        Relayout();
        OnPropertyChanged(nameof(Loop));
        OnPropertyChanged(nameof(LengthSeconds));
        OnPropertyChanged(nameof(UndoLabel));
        OnPropertyChanged(nameof(RedoLabel));
        Revision++;
        _playback.Recompile();
    }

    void SelectionChanged()
    {
        Selection = new HashSet<Guid>(_selection);
        PointSelectionChanged();
        OnPropertyChanged(nameof(SelectionLabel));
        var single = _selection.Count == 1 ? FindEvent(_selection.First()) : null;
        if (Properties?.Event != single)
            Properties = single is null ? null : new EventPropertiesViewModel(single, _session, History, _pickAudioFile, _playback, ResizeEvent);
        else
            Properties?.Refresh();
    }

    void RebuildTracks()
    {
        var selectedTrack = SelectedTrack?.Track;
        Tracks.Clear();
        foreach (var track in SelectedTimeline?.Tracks ?? []) Tracks.Add(new TrackRowViewModel(track, this));
        SelectedTrack = Tracks.FirstOrDefault(t => t.Track == selectedTrack) ?? Tracks.FirstOrDefault();
        Relayout();
    }

    void RebuildTracksIfChanged()
    {
        var current = SelectedTimeline?.Tracks ?? [];
        if (!current.SequenceEqual(Tracks.Select(t => t.Track))) RebuildTracks();
    }

    public TimeSpan SnapTime(TimeSpan time)
    {
        var grid = Snap.Interval;
        if (grid <= TimeSpan.Zero) return time;
        return TimeSpan.FromTicks((long)Math.Round((double)time.Ticks / grid.Ticks) * grid.Ticks);
    }

    /// <summary>How long an event lasts on screen: a lighting block's length, audio length (or the file's), zero for markers.</summary>
    public TimeSpan LengthOf(TimelineEvent e) => e switch
    {
        LightingClip clip => clip.Length,
        AudioEvent audio => audio.Length ?? AudioFileLength(audio) - audio.Offset ?? TimeSpan.Zero,
        _ => TimeSpan.Zero,
    };

    public TimeSpan? AudioFileLength(AudioEvent audio) => string.IsNullOrWhiteSpace(audio.File)
        ? null
        : _playback.AudioLength(AudioPaths.Resolve(audio.File, _session.ShowFolder));

    public string? FixtureName(Guid id) => _session.Project.Fixtures.FirstOrDefault(f => f.Id == id)?.Name;

    TimeSpan EndOf(TimelineEvent e) => e.Start + LengthOf(e);

    TimeSpan ContentLength() => SelectedTimeline?.Length ?? AllEvents().Select(EndOf).DefaultIfEmpty(TimeSpan.FromSeconds(30)).Max();

    IEnumerable<TimelineEvent> AllEvents() => (SelectedTimeline?.Tracks ?? []).SelectMany(t => t.Events);

    IEnumerable<TimelineEvent> SelectedEvents() => AllEvents().Where(e => _selection.Contains(e.Id));

    TimelineEvent? FindEvent(Guid id) => AllEvents().FirstOrDefault(e => e.Id == id);

    TimelineTrack? TrackContaining(TimelineEvent e) => SelectedTimeline?.Tracks.FirstOrDefault(t => t.Events.Contains(e));

    int? FindTrackOf(Guid id)
    {
        for (int i = 0; i < Tracks.Count; i++) if (Tracks[i].Track.Events.Any(e => e.Id == id)) return i;
        return null;
    }

    static TrackKind KindOf(TimelineEvent e) => e switch { LightingClip => TrackKind.Lighting, AudioEvent => TrackKind.Audio, _ => TrackKind.Markers };

    static string Describe(TimelineEvent e) => e switch { LightingClip => "lighting block", AudioEvent => "audio event", _ => "marker" };
}

public sealed record SnapOption(string Label, TimeSpan Interval)
{
    public override string ToString() => Label;
}
