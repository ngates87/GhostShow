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
/// it's expanded (tall, its clips showing their envelopes). Edits go through the history like everything else.
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

    public bool IsAudio => Track.Kind == TrackKind.Audio;

    /// <summary>Lighting: tall, with each clip's envelopes drawn inside it (where handles are edited).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Height), nameof(ExpandGlyph), nameof(ShowSubtitle))]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value) => _editor.SetExpanded(this, value);

    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    public ObservableCollection<LaneRowViewModel> Lanes { get; } = new();

    /// <summary>The header's height; the canvas draws the track's row at the same height.</summary>
    public double Height => TimelineEditorViewModel.HeightOf(Track, IsExpanded);

    public string ChannelSummary => Lanes.Count switch { 0 => "No channels: right-click → Channels…", 1 => "1 channel", var n => $"{n} channels" };

    /// <summary>Under the name, where there's room: what the track controls (lighting) or its kind.</summary>
    public string Subtitle => IsLighting ? ChannelSummary : KindLabel;

    public bool ShowSubtitle => IsLighting && Height > TimelineEditorViewModel.CollapsedTrackHeight;

    [RelayCommand]
    void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    void EditChannels() => _editor.EditChannels(this);

    [RelayCommand]
    void EditProperties() => _editor.EditTrackProperties(this);

    // ---- the header's right-click menu, and renaming in place (double-click or F2) ----

    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    [ObservableProperty]
    public partial string EditName { get; set; } = "";

    [RelayCommand]
    void StartRename()
    {
        EditName = Name;
        IsRenaming = true;
    }

    /// <summary>Enter, or leaving the box: an empty name keeps the old one.</summary>
    public void CommitRename()
    {
        if (!IsRenaming) return;
        IsRenaming = false;
        if (EditName.Trim() is { Length: > 0 } name) Name = name;
    }

    public void CancelRename() => IsRenaming = false;

    public string MuteLabel => Enabled ? "Mute" : "Unmute";

    [RelayCommand]
    void ToggleMute() => Enabled = !Enabled;

    [RelayCommand]
    void MoveUp() => _editor.MoveTrack(this, -1);

    [RelayCommand]
    void MoveDown() => _editor.MoveTrack(this, +1);

    [RelayCommand]
    void Remove() => _editor.RemoveTrack(this);

    public TrackKind Kind => Track.Kind;

    public string KindLabel => Track.Kind switch { TrackKind.Lighting => "Lighting", _ => "Audio" };

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

    /// <summary>Audio: the track's volume, 0-100 (the header's slider).</summary>
    public double VolumePercent
    {
        get => Math.Round(Track.Volume * 100);
        set => editor.SetTrackVolume(Track, value / 100);
    }

    public string VolumeLabel => $"{VolumePercent:0}%";

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
        OnPropertyChanged(nameof(MuteLabel));
        OnPropertyChanged(nameof(VolumePercent));
        OnPropertyChanged(nameof(VolumeLabel));
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
        OnPropertyChanged(nameof(Subtitle));
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
        playback.Open = id =>
        {
            if (session.Project.Timelines.FirstOrDefault(t => t.Id == id) is { } timeline) OpenTimeline(timeline);
        };
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
        (0, 1) => "1 handle selected",
        (0, var p) => $"{p} handles selected",
        (var n, _) => $"{n} {SelectedKind(n)} selected",
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

    // ---- playing alongside other timelines (see TimelinePlayback) ----

    static readonly string[] PlaybackProperties =
        [nameof(Priority), nameof(PauseOverridden), nameof(ReleaseFadeSeconds), nameof(DuckPercent), nameof(ReleaseAtEnd), nameof(PlaybackSummary)];

    void SetPlayback(string description, string field, Func<TimelinePlayback, TimelinePlayback> change)
    {
        if (SelectedTimeline is not { } timeline) return;
        var (old, updated) = (timeline.Playback, change(timeline.Playback));
        if (old == updated) return;
        History.Execute(description, () => timeline.Playback = updated, () => timeline.Playback = old, (timeline.Id, "playback", field));
    }

    /// <summary>Higher wins channels shared with other playing timelines (0 normal; e.g. 10 for scares).</summary>
    public decimal Priority
    {
        get => SelectedTimeline?.Playback.Priority ?? 0;
        set => SetPlayback("Change priority", "priority", p => p with { Priority = (int)value });
    }

    /// <summary>While this plays, lower-priority timelines sharing a channel pause (otherwise they keep running underneath).</summary>
    public bool PauseOverridden
    {
        get => SelectedTimeline?.Playback.PauseOverridden ?? false;
        set => SetPlayback(value ? "Pause what it overrides" : "Keep what it overrides running", "pause", p => p with { PauseOverridden = value });
    }

    public decimal ReleaseFadeSeconds
    {
        get => (decimal)(SelectedTimeline?.Playback.ReleaseFade.TotalSeconds ?? 0);
        set => SetPlayback("Change release fade", "release", p => p with { ReleaseFade = TimeSpan.FromSeconds((double)Math.Max(0, value)) });
    }

    /// <summary>Lower-priority timelines' sounds while this plays, in percent (100 = untouched).</summary>
    public decimal DuckPercent
    {
        get => (decimal)Math.Round((SelectedTimeline?.Playback.DuckOthers ?? 1) * 100);
        set => SetPlayback("Change ducking", "duck", p => p with { DuckOthers = (double)Math.Clamp(value, 0, 100) / 100 });
    }

    public bool ReleaseAtEnd
    {
        get => SelectedTimeline?.Playback.ReleaseAtEnd ?? false;
        set => SetPlayback(value ? "Release at the end" : "Hold at the end", "end", p => p with { ReleaseAtEnd = value });
    }

    /// <summary>On the settings button: "Normal", or "Priority 10 · pauses others · releases at end".</summary>
    public string PlaybackSummary
    {
        get
        {
            if (SelectedTimeline?.Playback is not { } p || p == new TimelinePlayback()) return "Normal";
            var parts = new List<string>();
            if (p.Priority != 0) parts.Add($"Priority {p.Priority}");
            if (p.PauseOverridden) parts.Add("pauses others");
            if (p.DuckOthers < 1) parts.Add($"ducks to {p.DuckOthers:P0}");
            if (p.ReleaseAtEnd) parts.Add("releases at end");
            if (p.ReleaseFade > TimeSpan.Zero) parts.Add($"{p.ReleaseFade.TotalSeconds:0.#} s release");
            return string.Join(" · ", parts);
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
        EnvelopeEditor?.Show(null);   // its clip was on the other timeline
        RebuildTracks();
        _playback.SetTimeline(value);
        OnPropertyChanged(nameof(TimelineName));
        OnPropertyChanged(nameof(Loop));
        OnPropertyChanged(nameof(LengthSeconds));
        foreach (var name in PlaybackProperties) OnPropertyChanged(name);
        OnPropertyChanged(nameof(UndoLabel));
        OnPropertyChanged(nameof(RedoLabel));
        Revision++;
    }

    partial void OnSelectedTrackChanged(TrackRowViewModel? oldValue, TrackRowViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        OnPropertyChanged(nameof(AddLabel));
        OnPropertyChanged(nameof(IsAudioTrackSelected));
    }

    /// <summary>The toolbar's Add button is for sounds; lighting clips (Level Control and effects) are in the Effects menu.</summary>
    public bool IsAudioTrackSelected => SelectedTrack?.Kind == TrackKind.Audio;

    /// <summary>The toolbar's Add button says what it adds to the selected track.</summary>
    public string AddLabel => SelectedTrack?.Kind switch
    {
        TrackKind.Lighting => "+ Level Control",
        TrackKind.Audio => "+ Sound…",
        _ => "+ Add",
    };

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
        _playback.Unload(timeline);
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

    /// <summary>
    /// Moves every selected clip, and those linked to them, by delta (each snapped to the grid, unless the caller
    /// already snapped the drag to an edge; none goes before 0). Nothing moves if one of them is locked. One undo step.
    /// </summary>
    public void MoveSelection(TimeSpan delta, bool snapToGrid = true)
    {
        var moving = MovingWith(_selection);
        var events = AllEvents().Where(e => moving.Contains(e.Id)).ToList();
        if (events.Count == 0 || delta == TimeSpan.Zero) return;
        var earliest = events.Min(e => e.Start);
        if (earliest + delta < TimeSpan.Zero) delta = -earliest;
        var before = events.ToDictionary(e => e, e => e.Start);
        var after = events.ToDictionary(e => e, e => snapToGrid ? SnapTime(e.Start + delta) : e.Start + delta);
        History.Execute(events.Count == 1 ? "Move clip" : $"Move {events.Count} clips",
                        () => { foreach (var (e, t) in after) e.Start = t; },
                        () => { foreach (var (e, t) in before) e.Start = t; });
    }

    /// <summary>Drag of a clip's right edge: an audio clip's length, or a lighting clip's (scaling or cropping its envelopes).</summary>
    public void ResizeEvent(Guid id, TimeSpan length) => ResizeEvent(id, length, snapToGrid: true);

    /// <param name="snapToGrid">False when the drag already snapped the edge (to the playhead or another clip).</param>
    public void ResizeEvent(Guid id, TimeSpan length, bool snapToGrid)
    {
        TimeSpan Snapped(TimeSpan start, TimeSpan l) => snapToGrid ? SnapTime(start + l) - start : l;
        switch (FindEvent(id))
        {
            case { Locked: true }:
                return;
            case AudioEvent audio:
                length = Snapped(audio.Start, length < TimeSpan.Zero ? TimeSpan.Zero : length);
                if (audio.Length == length || length <= TimeSpan.Zero) return;
                var oldLength = audio.Length;
                History.Execute("Change audio length", () => audio.Length = length, () => audio.Length = oldLength);
                break;
            case LightingClip clip:
                ResizeClip(clip, clip.Start, Snapped(clip.Start, length));
                break;
        }
    }

    /// <summary>
    /// Drag of a clip's left edge to a new start (snapped), its end kept: a lighting clip's envelopes scale or are
    /// cropped; an audio clip is trimmed, starting later (or earlier) into its file.
    /// </summary>
    public void ResizeEventStart(Guid id, TimeSpan start, bool snapToGrid = true)
    {
        start = start < TimeSpan.Zero ? TimeSpan.Zero : start;
        if (snapToGrid) start = SnapTime(start);
        switch (FindEvent(id))
        {
            case { Locked: true }:
                return;
            case LightingClip clip:
                var end = clip.Start + clip.Length;
                if (start < end) ResizeClip(clip, start, end - start);
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

    /// <summary>Adds a clip of the selected track's kind at the playhead (lighting: a Level Control clip).</summary>
    [RelayCommand]
    Task AddEvent() => AddEventToSelectedTrackAsync(SnapTime(_playback.Position));

    async Task AddEventToSelectedTrackAsync(TimeSpan at)
    {
        if (SelectedTrack is not { } row) return;
        if (row.IsLighting)
        {
            AddClipAt(row, at);
            return;
        }
        if (await NewAudioEventAsync(at) is not { } e) return;
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

    /// <summary>Deletes the selected clips (except locked ones), or the selected handles.</summary>
    [RelayCommand]
    void Delete()
    {
        if (_selectedPoints.Count > 0)
        {
            DeleteSelectedPoints();
            return;
        }
        var removals = SelectedEvents().Where(e => !e.Locked).Select(e => (Track: TrackContaining(e)!, Event: e, Index: TrackContaining(e)!.Events.IndexOf(e))).ToList();
        if (removals.Count == 0) return;
        History.Execute(removals.Count == 1 ? $"Delete {Describe(removals[0].Event)}" : $"Delete {removals.Count} clips",
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
        OnPropertyChanged(nameof(CanPaste));
    }

    /// <summary>Pastes the copied events at the playhead, keeping their spacing; onto their own tracks when the kind matches.</summary>
    [RelayCommand]
    void Paste() => PasteAt(_playback.Position);

    public bool CanPaste => _clipboard.Count > 0;

    /// <summary>Pastes the copied events starting at a time (the right-click menu's "Paste here").</summary>
    public void PasteAt(TimeSpan time)
    {
        if (_clipboard.Count == 0 || Tracks.Count == 0) return;
        var earliest = _clipboard.Min(c => c.Event.Start);
        var at = SnapTime(time < TimeSpan.Zero ? TimeSpan.Zero : time);
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
        History.Execute(copies.Count == 1 ? $"{verb} {Describe(copies[0].Copy)}" : $"{verb} {copies.Count} clips",
                        () => { foreach (var c in copies) c.Track.Events.Add(c.Copy); },
                        () => { foreach (var c in copies) c.Track.Events.Remove(c.Copy); });
        _selection.Clear();
        foreach (var c in copies) _selection.Add(c.Copy.Id);
        SelectionChanged();
    }

    [RelayCommand]
    void Split() => SplitAtPlayhead();

    [RelayCommand]
    void Lock() => ToggleLock();

    [RelayCommand]
    void LinkClips() => Link();

    [RelayCommand]
    void UnlinkClips() => Unlink();

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
        var track = new TimelineTrack { Kind = kind, Name = kind switch { TrackKind.Lighting => $"Lighting {count}", _ => $"Audio {count}" } };
        History.Execute("Add track", () => timeline.Tracks.Add(track), () => timeline.Tracks.Remove(track));
        SelectedTrack = Tracks.FirstOrDefault(t => t.Track == track);
    }

    [RelayCommand]
    void RemoveTrack()
    {
        if (SelectedTrack is { } row) RemoveTrack(row);
    }

    /// <summary>Removes a track (undoable), e.g. from its header's right-click menu.</summary>
    public void RemoveTrack(TrackRowViewModel row)
    {
        if (SelectedTimeline is not { } timeline) return;
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
        if (SelectedTrack is { } row) MoveTrack(row, direction);
    }

    /// <summary>An audio track's volume (0-1, multiplied into its clips'). Dragging the slider makes one undo step.</summary>
    public void SetTrackVolume(TimelineTrack track, double volume)
    {
        volume = Math.Round(Math.Clamp(volume, 0, 1), 2);
        if (track.Volume == volume) return;
        double old = track.Volume;
        History.Execute("Change track volume", () => track.Volume = volume, () => track.Volume = old, (track.Id, "volume"));
    }

    /// <summary>Moves a track up (-1) or down (+1), keeping it selected.</summary>
    public void MoveTrack(TrackRowViewModel row, int direction)
    {
        if (SelectedTimeline is not { } timeline) return;
        SelectedTrack = row;
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
        foreach (var name in PlaybackProperties) OnPropertyChanged(name);
        OnPropertyChanged(nameof(UndoLabel));
        OnPropertyChanged(nameof(RedoLabel));
        Revision++;
        TrackProperties?.Refresh();
        EnvelopeEditor?.Refresh();
        _playback.Recompile();
    }

    void SelectionChanged()
    {
        Selection = new HashSet<Guid>(_selection);
        if (_selection.Count > 0 || _selectedPoints.Count > 0) TrackProperties = null;   // the panel shows what was picked
        PointSelectionChanged();
        OnPropertyChanged(nameof(SelectionLabel));
        OnPropertyChanged(nameof(CanLink));
        OnPropertyChanged(nameof(CanUnlink));
        EnvelopeEditorFollowSelection();
        var single = _selection.Count == 1 ? FindEvent(_selection.First()) : null;
        if (Properties?.Event != single)
            Properties = single is null ? null : new EventPropertiesViewModel(single, _session, History, _pickAudioFile, _playback, ResizeEvent, this);
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

    /// <summary>How long a clip lasts on screen: a lighting clip's length, audio length (or the file's).</summary>
    public TimeSpan LengthOf(TimelineEvent e) => e switch
    {
        LightingClip clip => clip.Length,
        AudioEvent audio => audio.Length ?? AudioFileLength(audio) - audio.Offset ?? TimeSpan.Zero,
        _ => TimeSpan.Zero,
    };

    public TimeSpan? AudioFileLength(AudioEvent audio) => string.IsNullOrWhiteSpace(audio.File)
        ? null
        : _playback.AudioLength(AudioPaths.Resolve(audio.File, _session.ShowFolder));

    public Waveform? WaveformOf(AudioEvent audio) => string.IsNullOrWhiteSpace(audio.File)
        ? null
        : _playback.AudioWaveform(AudioPaths.Resolve(audio.File, _session.ShowFolder));

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

    static TrackKind KindOf(TimelineEvent e) => e switch { LightingClip => TrackKind.Lighting, _ => TrackKind.Audio };

    /// <summary>"clip", "sounds", or "items" for a mix.</summary>
    string SelectedKind(int count)
    {
        var kinds = SelectedEvents().Select(e => e switch { LightingClip => "clip", _ => "sound" }).Distinct().ToList();
        string kind = kinds.Count == 1 ? kinds[0] : "item";
        return count == 1 ? kind : kind + "s";
    }

    static string Describe(TimelineEvent e) => e switch
    {
        LightingClip { Effect: not null } clip => $"{clip.KindName} effect",
        LightingClip => "Level Control clip",
        _ => "audio clip",
    };
}

public sealed record SnapOption(string Label, TimeSpan Interval)
{
    public override string ToString() => Label;
}
