using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Timeline;

namespace GhostShow.App.ViewModels;

/// <summary>
/// VenueMagic's Envelope Editor, in its own window: one Level Control clip's envelopes, large. The channels are listed
/// by fixture with checkboxes (unticked ones aren't drawn) and their colours; one of them is the active channel
/// (click it in the list, or select one of its handles), drawn on top, and new handles go to it. The plot shows the
/// clip (and, dimmed, the levels just before and after it). Handles are added (double-click: on a curve, to that
/// channel; elsewhere, to the active channel where clicked), selected (click, Ctrl+click, a box), dragged
/// (optionally locked to time or to level), given a curve type and deleted, with exact values in the fields below. Every edit goes through the timeline editor, so it's undoable, follows the same rules as editing on
/// the timeline, and plays straight away. The window follows the timeline: selecting another lighting clip (or one of
/// its handles) shows that clip.
/// </summary>
public sealed partial class EnvelopeEditorViewModel : ObservableObject
{
    readonly HashSet<LaneTarget> _hidden = [];   // channels unticked (kept while the window is open, across clips)

    public EnvelopeEditorViewModel(TimelineEditorViewModel editor)
    {
        Editor = editor;
    }

    public TimelineEditorViewModel Editor { get; }

    /// <summary>The clip being edited; null when there's none (it was deleted, or the timeline was closed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(HasClip), nameof(Track))]
    public partial LightingClip? Clip { get; private set; }

    public bool HasClip => Clip is not null;

    public TimelineTrack? Track => Clip is null ? null : Editor.SelectedTimeline?.Tracks.FirstOrDefault(t => t.Events.Contains(Clip));

    public string Title => Clip is null
        ? "Envelope Editor"
        : $"Envelope Editor · {Clip.Name ?? Clip.KindName} · {Track?.Name} · {PlaybackViewModel.Format(Clip.Start)}–{PlaybackViewModel.Format(Clip.Start + Clip.Length)}"
          + (Clip.Effect is not null ? " · an effect: view only (convert it to Level Control to edit its handles)" : "");

    /// <summary>The track's channels by fixture, with whether each is drawn.</summary>
    public ObservableCollection<EnvelopeFixtureViewModel> Fixtures { get; } = new();

    /// <summary>What the plot shows, relative to the clip's start (it can start before 0 to show what leads in).</summary>
    [ObservableProperty]
    public partial TimeSpan ViewStart { get; set; }

    [ObservableProperty]
    public partial TimeSpan ViewLength { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Dragging changes only the level (handles keep their times).</summary>
    [ObservableProperty]
    public partial bool LockTime { get; set; }

    /// <summary>Dragging changes only the time (handles keep their levels).</summary>
    [ObservableProperty]
    public partial bool LockLevel { get; set; }

    /// <summary>
    /// While stopped or paused, selecting or moving a handle puts the playhead there (paused), so the lights show the
    /// level being set. Off by default: it drives the real fixtures.
    /// </summary>
    [ObservableProperty]
    public partial bool UpdateLamps { get; set; }

    /// <summary>The channel new handles go to (double-click away from the curves); drawn on top. Null when none is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveLabel))]
    public partial LaneTarget? ActiveTarget { get; private set; }

    public string ActiveLabel => ActiveTarget is { } target ? Editor.LaneLabel(target) : "";

    partial void OnActiveTargetChanged(LaneTarget? value)
    {
        foreach (var fixture in Fixtures) fixture.Refresh();
        Revision++;
    }

    /// <summary>Makes a channel the active one (showing it, if it was hidden).</summary>
    public void SetActive(LaneTarget target)
    {
        _hidden.Remove(target);
        ActiveTarget = target;
        foreach (var fixture in Fixtures) fixture.Refresh();
    }

    /// <summary>The active channel as it plays, if it's shown.</summary>
    public TrackLevels? ActiveLevels => ShownLevels.FirstOrDefault(l => l.Channel.Target == ActiveTarget);

    /// <summary>Keeps the active channel one that's on the track and shown (else the first shown).</summary>
    void KeepActiveValid()
    {
        var shown = ShownLevels;
        if (ActiveTarget is null || shown.All(l => l.Channel.Target != ActiveTarget)) ActiveTarget = shown.FirstOrDefault()?.Channel.Target;
    }

    /// <summary>Bumped when the plot should redraw for something the timeline editor doesn't report (channels shown or hidden, the view).</summary>
    [ObservableProperty]
    public partial long Revision { get; private set; }

    partial void OnViewStartChanged(TimeSpan value) => Revision++;
    partial void OnViewLengthChanged(TimeSpan value) => Revision++;

    // ---- the clip ----

    /// <summary>Shows a clip (fitting it in the view if it's a different one).</summary>
    public void Show(LightingClip? clip)
    {
        if (clip == Clip)
        {
            Refresh();
            return;
        }
        Clip = clip;
        RebuildChannels();
        Fit();
    }

    /// <summary>After any edit: the clip may have been deleted, renamed, moved or resized, or the track's channels changed.</summary>
    public void Refresh()
    {
        if (Clip is not null && Track is null)
        {
            Clip = null;
            RebuildChannels();
            return;
        }
        OnPropertyChanged(nameof(Title));
        if (!Fixtures.SelectMany(f => f.Channels).Select(c => c.Channel).SequenceEqual(Track?.Channels ?? [])) RebuildChannels();
        Revision++;
    }

    void RebuildChannels()
    {
        Fixtures.Clear();
        foreach (var group in (Track?.Channels ?? []).GroupBy(c => c.Target.FixtureId))
        {
            string name = group.Key is { } id ? Editor.FixtureName(id) ?? "Missing fixture" : "Raw DMX channels";
            Fixtures.Add(new EnvelopeFixtureViewModel(name, group.Select(c => new EnvelopeChannelViewModel(c, ShortLabel(c), this)).ToList(), this));
        }
        KeepActiveValid();
        Revision++;
    }

    /// <summary>"Red", "Ch 2: Fog", "U1 · 37": the label without the fixture's name (it's the group's).</summary>
    string ShortLabel(ChannelLane channel)
    {
        string label = Editor.LaneLabel(channel.Target);
        return channel.Target.FixtureId is null ? label : label[(label.IndexOf(" · ", StringComparison.Ordinal) + 3)..];
    }

    public bool IsShown(LaneTarget target) => !_hidden.Contains(target);

    internal void SetShown(LaneTarget target, bool shown)
    {
        if (shown ? !_hidden.Remove(target) : !_hidden.Add(target)) return;
        KeepActiveValid();
        foreach (var fixture in Fixtures) fixture.Refresh();
        Revision++;
    }

    /// <summary>The track's channels as they play, the ones ticked (in the track's order).</summary>
    public IReadOnlyList<TrackLevels> ShownLevels => Track is { } track ? Editor.LevelsOf(track).Where(l => IsShown(l.Channel.Target)).ToList() : [];

    [RelayCommand]
    void ShowAll()
    {
        _hidden.Clear();
        KeepActiveValid();
        foreach (var fixture in Fixtures) fixture.Refresh();
        Revision++;
    }

    // ---- moving between clips ----

    IReadOnlyList<LightingClip> TrackClips => Track?.Events.OfType<LightingClip>().OrderBy(c => c.Start).ToList() ?? [];

    [RelayCommand]
    void PreviousClip() => Step(-1);

    [RelayCommand]
    void NextClip() => Step(+1);

    void Step(int direction)
    {
        var clips = TrackClips;
        if (Clip is null || clips.Count == 0) return;
        int index = clips.ToList().IndexOf(Clip) + direction;
        if (index < 0 || index >= clips.Count) return;
        Editor.SelectEvent(clips[index].Id, additive: false);   // the window follows the selection
    }

    // ---- the view ----

    /// <summary>The whole clip, with a little of what's either side.</summary>
    [RelayCommand]
    public void Fit()
    {
        var length = Clip?.Length ?? TimeSpan.FromSeconds(4);
        var margin = length * 0.06;
        (ViewStart, ViewLength) = (-margin, length + margin * 2);
    }

    [RelayCommand]
    void ZoomIn() => ZoomAround(ViewStart + ViewLength / 2, 1 / 1.5);

    [RelayCommand]
    void ZoomOut() => ZoomAround(ViewStart + ViewLength / 2, 1.5);

    /// <summary>Zooms by a factor (below 1: in), keeping a time where it is on screen.</summary>
    public void ZoomAround(TimeSpan at, double factor)
    {
        var length = TimeSpan.FromTicks((long)Math.Clamp(ViewLength.Ticks * factor, TimeSpan.FromMilliseconds(50).Ticks, TimeSpan.FromHours(2).Ticks));
        double fraction = ViewLength > TimeSpan.Zero ? (double)(at - ViewStart).Ticks / ViewLength.Ticks : 0.5;
        (ViewStart, ViewLength) = (at - length * fraction, length);
    }

    public void Pan(TimeSpan by) => ViewStart += by;

    // ---- handles ----

    /// <summary>
    /// A handle on a channel's envelope at a time (relative to the clip): at the level the curve has there (a
    /// double-click on the curve: nothing changes yet), or at a given level. The channel becomes the active one.
    /// </summary>
    public CurvePoint? AddHandle(LaneTarget target, TimeSpan relative, byte? level = null)
    {
        SetActive(target);
        if (Clip is null || Editor.AddHandle(Clip, target, Clip.Start + relative) is not { } handle) return null;
        if (level is { } value && value != handle.Value) Editor.PointProperties?.Dmx = value;
        AfterHandleChange();
        return handle;
    }

    /// <summary>Ctrl+A: every handle of the ticked channels in this clip.</summary>
    [RelayCommand]
    void SelectAllHandles()
    {
        if (Clip is null) return;
        Editor.SelectPoints(Clip.Lanes.Where(l => IsShown(l.Target)).SelectMany(l => l.Points), additive: false);
    }

    [RelayCommand]
    void DeleteHandles() => Editor.DeleteCommand.Execute(null);

    public static IReadOnlyList<CurveKind> Curves => PointPropertiesViewModel.Curves;

    /// <summary>For several selected handles: one curve for them all.</summary>
    [RelayCommand]
    void SetCurve(CurveKind curve) => Editor.SetCurve(Editor.SelectedPoints, curve);

    public bool HasSeveralSelected => Editor.SelectedPoints.Count > 1;

    /// <summary>After selecting or moving handles: with "update lamps" on, the lights show the level at the (first) selected handle.</summary>
    public void AfterHandleChange()
    {
        OnPropertyChanged(nameof(HasSeveralSelected));
        // Selecting a handle makes its channel the active one.
        if (Editor.SelectedPoints.Count > 0 && Editor.LaneOf(Editor.SelectedPoints.First()) is { } lane && lane.Target != ActiveTarget && IsShown(lane.Target))
            ActiveTarget = lane.Target;
        if (!UpdateLamps || Editor.SelectedPoints.FirstOrDefault() is not { } point) return;
        Editor.Playback.Cue(Editor.AbsoluteTime(point));
    }
}

/// <summary>A fixture in the Envelope Editor's channel list: ticking it shows or hides all its channels.</summary>
public sealed partial class EnvelopeFixtureViewModel(string name, IReadOnlyList<EnvelopeChannelViewModel> channels, EnvelopeEditorViewModel owner) : ObservableObject
{
    public string Name { get; } = name;
    public IReadOnlyList<EnvelopeChannelViewModel> Channels { get; } = channels;

    /// <summary>True: all shown; false: none; null: some.</summary>
    public bool? IsChecked
    {
        get => Channels.All(c => c.IsChecked) ? true : Channels.Any(c => c.IsChecked) ? null : false;
        set
        {
            bool show = value ?? false;
            foreach (var channel in Channels) owner.SetShown(channel.Channel.Target, show);
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsChecked));
        foreach (var channel in Channels) channel.Refresh();
    }
}

public sealed partial class EnvelopeChannelViewModel(ChannelLane channel, string label, EnvelopeEditorViewModel owner) : ObservableObject
{
    public ChannelLane Channel { get; } = channel;
    public string Label { get; } = label;
    public string Color => TimelineEditorViewModel.LaneColor(Channel);

    public bool IsChecked
    {
        get => owner.IsShown(Channel.Target);
        set => owner.SetShown(Channel.Target, value);
    }

    /// <summary>New handles go to this channel.</summary>
    public bool IsActive => owner.ActiveTarget == Channel.Target;

    [RelayCommand]
    void MakeActive() => owner.SetActive(Channel.Target);

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(IsActive));
    }
}
