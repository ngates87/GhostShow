using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Playback;
using GhostShow.Core.Timeline;

namespace GhostShow.App.ViewModels;

/// <summary>A timeline in the "Playing" list: what's running, where it is, and a Stop button.</summary>
public sealed partial class RunningRowViewModel(Guid id, PlaybackViewModel owner) : ObservableObject
{
    public Guid Id { get; } = id;

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Detail { get; set; } = "";

    [ObservableProperty]
    public partial bool IsFocused { get; set; }

    [RelayCommand]
    void Stop() => owner.StopTimeline(Id);

    [RelayCommand]
    void Open() => owner.OpenTimeline(Id);
}

/// <summary>
/// The transport and what's playing, as the screens show them. Several timelines can play at once; the transport
/// controls the <em>focused</em> one (the timeline open in the editor), and the "Playing" list shows them all.
///
/// The playing itself happens on the show computer, where the DMX adapters and speakers are plugged in; this only asks
/// <see cref="IPlayback"/> to do things and shows its status. On the show computer that's <see cref="ShowPlayback"/>
/// (it plays); in the browser build it's RemotePlayback (it sends the request over the network). This class is the
/// same in both.
/// </summary>
public sealed partial class PlaybackViewModel : ObservableObject
{
    ShowTimeline? _timeline;

    public PlaybackViewModel(IPlayback playback)
    {
        Show = playback;
        Show.Compiled += id =>
        {
            if (id == _timeline?.Id) ShowIssues();
        };
    }

    /// <summary>Playback: the real thing on the show computer, or the show computer's, over the network.</summary>
    public IPlayback Show { get; }

    /// <summary>The focused timeline: the one the transport controls.</summary>
    public ShowTimeline? Timeline => _timeline;

    volatile object? _focusedId;   // boxed Guid, for other threads (the web remote's status)

    /// <summary>The focused timeline's ID; safe to read from any thread.</summary>
    public Guid? FocusedId => _focusedId is Guid id ? id : null;

    /// <summary>Problems in the focused timeline (missing fixtures or files, ...); those parts are skipped.</summary>
    public ObservableCollection<TimelineIssue> Issues { get; } = new();

    /// <summary>Every timeline playing or paused, highest priority first.</summary>
    public ObservableCollection<RunningRowViewModel> Running { get; } = new();

    /// <summary>Opens a timeline in the editor (a "Playing" row's name; set by the editor).</summary>
    public Action<Guid>? Open { get; set; }

    /// <summary>Raised after every recompile of the focused timeline, so the editor can mark parts with problems.</summary>
    public event Action? Compiled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlaying), nameof(PlayPauseLabel))]
    public partial TransportState State { get; private set; }

    [ObservableProperty]
    public partial TimeSpan Position { get; private set; }

    [ObservableProperty]
    public partial TimeSpan Length { get; private set; }

    [ObservableProperty]
    public partial string StateLabel { get; private set; } = "Stopped";

    [ObservableProperty]
    public partial string TimeLabel { get; private set; } = "00:00.0 / 00:00.0";

    public bool IsPlaying => State == TransportState.Playing;
    public string PlayPauseLabel => IsPlaying ? "❚❚ Pause" : "▶ Play";

    /// <summary>
    /// Focuses a timeline (the one open in the editor): the transport now controls it. Whatever else is playing
    /// carries on.
    /// </summary>
    public void SetTimeline(ShowTimeline? timeline)
    {
        if (timeline == _timeline) return;
        _timeline = timeline;
        _focusedId = timeline?.Id;
        Recompile();
    }

    /// <summary>Compiles the focused timeline again and swaps it into its player.</summary>
    public void Recompile()
    {
        if (_timeline is not null) Show.Load(_timeline.Id);
        else ShowIssues();
        Tick();
    }

    void ShowIssues()
    {
        Issues.Clear();
        foreach (var issue in _timeline is null ? [] : Show.IssuesOf(_timeline.Id)) Issues.Add(issue);
        Compiled?.Invoke();
    }

    /// <summary>Loads a timeline for playback if it isn't yet, so it can play without being open in the editor.</summary>
    public void EnsureLoaded(ShowTimeline timeline) => Show.EnsureLoaded(timeline.Id);

    /// <summary>
    /// Runs a timeline from its start (a show button): as a new copy if it's already running, as VenueMagic does,
    /// or not at all if it plays one at a time and is still playing. Returns whether it started.
    /// </summary>
    public bool Launch(ShowTimeline timeline)
    {
        bool started = Show.Launch(timeline.Id);
        Tick();
        return started;
    }

    /// <summary>Stops a timeline and every copy of it playing.</summary>
    public void StopTimeline(ShowTimeline timeline)
    {
        Show.StopTimeline(timeline.Id);
        Tick();
    }

    public bool IsRunning(Guid timelineId) => Show.IsRunning(timelineId);

    /// <summary>A new or opened show: everything stops at once (no release fades) and is forgotten.</summary>
    public void Reset()
    {
        Show.Reset();
        _timeline = null;
        _focusedId = null;
    }

    [RelayCommand]
    void PlayPause()
    {
        if (_timeline is null) return;
        Show.PlayOrPause(_timeline.Id);
        Tick();
    }

    /// <summary>Stops the focused timeline: its channels release (with its release fade) and its sounds stop.</summary>
    [RelayCommand]
    void Stop()
    {
        if (_timeline is not null) StopTimeline(_timeline.Id);
    }

    [RelayCommand]
    void Restart()
    {
        if (_timeline is null) return;
        Show.Restart(_timeline.Id);
        Tick();
    }

    /// <summary>Back to the start, playing or not (Home).</summary>
    [RelayCommand]
    void ToStart() => Seek(TimeSpan.Zero);

    /// <summary>Stops every timeline (each with its release fade).</summary>
    [RelayCommand]
    void StopAll()
    {
        Show.StopAll();
        Tick();
    }

    /// <summary>Stops one player (a row of the Playing list: a timeline, or a copy of one).</summary>
    public void StopTimeline(Guid playerId)
    {
        Show.StopPlayer(playerId);
        Tick();
    }

    public void OpenTimeline(Guid id) => Open?.Invoke(id);

    public void Seek(TimeSpan position)
    {
        if (_timeline is null) return;
        Show.Seek(_timeline.Id, position);
        Tick();
    }

    /// <summary>
    /// Shows the open timeline's look at a time: seeks there and, if it was stopped, holds it there paused (so its
    /// levels drive the lights). While playing, nothing changes. For "update lamps while editing".
    /// </summary>
    public void Cue(TimeSpan position)
    {
        if (_timeline is null) return;
        Show.Cue(_timeline.Id, position);
        Tick();
    }

    /// <summary>Called by the UI's refresh timer (30 Hz): reads the players' status for display. Never drives playback.</summary>
    public void Tick()
    {
        var status = _timeline is null ? default : Show.StatusOf(_timeline.Id);
        State = status.State;
        Position = status.Position;
        Length = status.Length;
        StateLabel = status switch
        {
            { AtEnd: true } => "End of show (holding)",
            { State: TransportState.Playing, Loop: true } => "Playing (looping)",
            { State: var state } => state.ToString(),
        };
        TimeLabel = $"{Format(status.Position)} / {Format(status.Length)}";
        RefreshRunning();
    }

    void RefreshRunning()
    {
        var running = Show.Running;
        if (!running.Select(r => r.Id).SequenceEqual(Running.Select(r => r.Id)))
        {
            Running.Clear();
            foreach (var r in running) Running.Add(new RunningRowViewModel(r.Id, this));
        }
        for (int i = 0; i < running.Count; i++)
        {
            var (r, row) = (running[i], Running[i]);
            row.Name = r.Id == r.TimelineId ? r.Name : $"{r.Name} (copy)";
            row.IsFocused = r.Id == _timeline?.Id;
            string state = r.Status.State == TransportState.Paused ? (r.Status.AtEnd ? "end" : "paused") : "▶";
            row.Detail = $"{state} {Format(r.Status.Position)}" + (r.Priority != 0 ? $" · priority {r.Priority}" : "");
        }
    }

    /// <summary>A sound file's length (named as the show names it), if known yet; asking starts measuring it.</summary>
    public TimeSpan? AudioLength(string file) => Show.AudioLength(file);

    /// <summary>A sound file's waveform for drawing; null until it has been measured (then the timeline redraws).</summary>
    public Waveform? AudioWaveform(string file) => Show.AudioWaveform(file);

    public static string Format(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss\.f") : time.ToString(@"mm\:ss\.f");
}
