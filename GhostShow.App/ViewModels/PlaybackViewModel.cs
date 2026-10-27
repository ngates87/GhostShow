using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Audio;
using GhostShow.Core.Playback;
using GhostShow.Core.Timeline;
using Microsoft.Extensions.Logging;

namespace GhostShow.App.ViewModels;

/// <summary>
/// The transport and what it's playing: which timeline is loaded into the player, keeping its compiled version up to
/// date (after any edit, patch change or newly measured audio length), and the state shown on the toolbar.
///
/// The player runs on its own thread; this only sends it commands and reads its status for display. Edits during
/// playback are allowed: the timeline is recompiled and swapped in, and takes effect on the next playback update.
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
        services.Player.MarkerReached += marker => Dispatcher.UIThread.Post(() => LastMarker = $"{marker.Name} at {Format(TimeSpan.FromTicks(marker.Time))}");
        services.Audio.LengthMeasured += _ => Dispatcher.UIThread.Post(Recompile);
        services.Session.Applied += Recompile;   // patch changes can make fixture events valid or invalid
    }

    public ShowTimeline? Timeline => _timeline;

    /// <summary>Problems in the loaded timeline (missing fixtures or files, ...); those events are skipped.</summary>
    public ObservableCollection<TimelineIssue> Issues { get; } = new();

    /// <summary>Raised after every recompile, so the editor can mark events with problems.</summary>
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

    [ObservableProperty]
    public partial string? LastMarker { get; private set; }

    public bool IsPlaying => State == TransportState.Playing;
    public string PlayPauseLabel => IsPlaying ? "❚❚ Pause" : "▶ Play";

    /// <summary>Loads a timeline into the player. Switching timelines stops the current one first.</summary>
    public void SetTimeline(ShowTimeline? timeline)
    {
        if (timeline == _timeline) return;
        if (_timeline is not null) _services.Player.Stop();
        _timeline = timeline;
        foreach (var file in AudioFiles()) _services.Audio.GetLength(file);   // start measuring now, in the background
        Recompile();
    }

    /// <summary>Recompiles the loaded timeline and swaps it into the player. Cheap enough to do on every edit.</summary>
    public void Recompile()
    {
        var compiled = _timeline is null ? CompiledTimeline.Empty : _services.Session.CompileTimeline(_timeline, _services.Audio.GetLength);
        _services.Player.Load(compiled);
        Issues.Clear();
        foreach (var issue in compiled.Issues) Issues.Add(issue);
        Compiled?.Invoke();
    }

    [RelayCommand]
    void PlayPause()
    {
        if (IsPlaying) _services.Player.Pause();
        else _services.Player.Play();
        Tick();
    }

    /// <summary>Stops playback: timeline control of the lights is released (manual levels return) and audio stops.</summary>
    [RelayCommand]
    void Stop()
    {
        _services.Player.Stop();
        Tick();
    }

    [RelayCommand]
    void Restart()
    {
        _services.Player.Restart();
        Tick();
    }

    public void Seek(TimeSpan position)
    {
        _services.Player.Seek(position);
        Tick();
    }

    /// <summary>Called by the UI's refresh timer (30 Hz): reads the player's status for display. Never drives playback.</summary>
    public void Tick()
    {
        var status = _services.Player.Status;
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
    }

    /// <summary>A file's measured length, if known yet (measuring starts in the background on first ask).</summary>
    public TimeSpan? AudioLength(string path) => _services.Audio.GetLength(path);

    IEnumerable<string> AudioFiles() =>
        (_timeline?.Tracks ?? []).SelectMany(t => t.Events).OfType<AudioEvent>()
            .Where(e => !string.IsNullOrWhiteSpace(e.File))
            .Select(e => AudioPaths.Resolve(e.File, _services.Session.ShowFolder));

    public static string Format(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss\.f") : time.ToString(@"mm\:ss\.f");
}
