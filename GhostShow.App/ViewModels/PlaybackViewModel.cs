using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Audio;
using GhostShow.Core.Playback;
using GhostShow.Core.Timeline;
using Microsoft.Extensions.Logging;

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
/// The transport and what's playing. Several timelines can play at once (see <see cref="PlaybackManager"/>); the
/// transport controls the <em>focused</em> one, the timeline open in the editor, and the "Playing" list shows them all.
/// Keeps every loaded timeline's compiled version up to date (after an edit, a patch change, or a newly measured audio
/// length): edits during playback take effect on the next playback update.
///
/// The players run on the playback thread; this only sends commands and reads their status for display.
/// </summary>
public sealed partial class PlaybackViewModel : ObservableObject
{
    readonly AppServices _services;
    readonly ILogger _logger;
    ShowTimeline? _timeline;

    public PlaybackViewModel(AppServices services, ILogger logger)
    {
        _services = services;
        _logger = logger;
        services.Audio.LengthMeasured += _ => Dispatcher.UIThread.Post(RecompileAll);
        services.Session.Applied += RecompileAll;   // patch changes can make fixture channels valid or invalid
    }

    PlaybackManager Manager => _services.Playback;

    /// <summary>The focused timeline: the one the transport controls.</summary>
    public ShowTimeline? Timeline => _timeline;

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
        foreach (var file in AudioFiles(timeline)) _services.Audio.GetLength(file);   // start measuring now, in the background
        Recompile();
    }

    /// <summary>Recompiles the focused timeline and swaps it into its player. Cheap enough to do on every edit.</summary>
    public void Recompile()
    {
        var compiled = Load(_timeline);
        Issues.Clear();
        foreach (var issue in compiled.Issues) Issues.Add(issue);
        Compiled?.Invoke();
        Tick();
    }

    /// <summary>After a patch change or a newly measured sound: every loaded timeline (so running ones stay right too).</summary>
    void RecompileAll()
    {
        foreach (var timeline in _services.Session.Project.Timelines.Where(t => t != _timeline && Manager.IsLoaded(t.Id))) Load(timeline);
        Recompile();
    }

    CompiledTimeline Load(ShowTimeline? timeline)
    {
        if (timeline is null) return CompiledTimeline.Empty;
        var compiled = _services.Session.CompileTimeline(timeline, _services.Audio.GetLength);
        Manager.Load(timeline.Id, timeline.Name, compiled);
        return compiled;
    }

    /// <summary>Loads a timeline for playback if it isn't yet (compiling it now), so it can play without being open in the editor.</summary>
    public void EnsureLoaded(ShowTimeline timeline)
    {
        if (Manager.IsLoaded(timeline.Id)) return;
        foreach (var file in AudioFiles(timeline)) _services.Audio.GetLength(file);
        Load(timeline);
    }

    /// <summary>
    /// Runs a timeline from its start (a show button): as a new copy if it's already running, as VenueMagic does,
    /// or, with playOnceOnly, not at all while it's running. Returns whether it started.
    /// </summary>
    public bool Launch(ShowTimeline timeline, bool playOnceOnly = false)
    {
        EnsureLoaded(timeline);
        if (playOnceOnly && Manager.IsRunning(timeline.Id)) return false;
        bool started = Manager.Launch(timeline.Id) is not null;
        Tick();
        return started;
    }

    /// <summary>Stops a timeline and every copy of it playing.</summary>
    public void StopTimeline(ShowTimeline timeline)
    {
        Manager.StopTimeline(timeline.Id);
        Tick();
    }

    public bool IsRunning(Guid timelineId) => Manager.IsRunning(timelineId);

    /// <summary>A timeline deleted from the show stops and is forgotten.</summary>
    public void Unload(ShowTimeline timeline) => Manager.Unload(timeline.Id);

    /// <summary>A new or opened show: everything stops at once (no release fades) and is forgotten.</summary>
    public void Reset()
    {
        Manager.UnloadAll();
        _timeline = null;
    }

    [RelayCommand]
    void PlayPause()
    {
        if (_timeline is null) return;
        if (IsPlaying) Manager.Pause(_timeline.Id);
        else Manager.Play(_timeline.Id);
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
        Manager.Restart(_timeline.Id);
        Tick();
    }

    /// <summary>Back to the start, playing or not (Home).</summary>
    [RelayCommand]
    void ToStart() => Seek(TimeSpan.Zero);

    /// <summary>Stops every timeline (each with its release fade).</summary>
    [RelayCommand]
    void StopAll()
    {
        Manager.StopAll();
        Tick();
    }

    public void StopTimeline(Guid id)
    {
        Manager.Stop(id);
        Tick();
    }

    public void OpenTimeline(Guid id) => Open?.Invoke(id);

    public void Seek(TimeSpan position)
    {
        if (_timeline is null) return;
        Manager.Seek(_timeline.Id, position);
        Tick();
    }

    /// <summary>
    /// Shows the open timeline's look at a time: seeks there and, if it was stopped, holds it there paused (so its
    /// levels drive the lights). While playing, nothing changes. For "update lamps while editing".
    /// </summary>
    public void Cue(TimeSpan position)
    {
        if (_timeline is null || IsPlaying) return;
        Manager.Seek(_timeline.Id, position);
        if (Manager.StatusOf(_timeline.Id).State == TransportState.Stopped)
        {
            Manager.Play(_timeline.Id);
            Manager.Pause(_timeline.Id);
            Manager.Seek(_timeline.Id, position);
        }
        Tick();
    }

    /// <summary>Called by the UI's refresh timer (30 Hz): reads the players' status for display. Never drives playback.</summary>
    public void Tick()
    {
        var status = _timeline is null ? default : Manager.StatusOf(_timeline.Id);
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
        var running = Manager.Running;
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

    /// <summary>A file's measured length, if known yet (measuring starts in the background on first ask).</summary>
    public TimeSpan? AudioLength(string path) => _services.Audio.GetLength(path);

    /// <summary>A file's waveform for drawing; null until it has been measured (which recompiles, so the timeline redraws).</summary>
    public Waveform? AudioWaveform(string path) => _services.Audio.GetWaveform(path);

    IEnumerable<string> AudioFiles(ShowTimeline? timeline) =>
        (timeline?.Tracks ?? []).SelectMany(t => t.Events).OfType<AudioEvent>()
            .Where(e => !string.IsNullOrWhiteSpace(e.File))
            .Select(e => AudioPaths.Resolve(e.File, _services.Session.ShowFolder));

    public static string Format(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss\.f") : time.ToString(@"mm\:ss\.f");
}
