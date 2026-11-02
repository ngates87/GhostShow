using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GhostShow.Audio;
using GhostShow.Core.Diagnostics;
using GhostShow.Core.Playback;
using GhostShow.Core.Timeline;
using GhostShow.Dmx;

namespace GhostShow.App.ViewModels;

public sealed record OutputTimingRow(string Universe, string Device, string Status, string Rate, string Intervals, string Frames, string LastError);

/// <summary>Output timing, playback and audio, UI responsiveness, memory, and recent log messages. Refreshed once a second.</summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    const int MaxLogEntries = 200;

    readonly OutputManager _outputs;
    readonly UiResponsivenessProbe _probe;
    readonly PlaybackLoop _loop;
    readonly PlaybackManager _player;
    readonly AudioService _audio;
    readonly PlaybackViewModel _playback;

    public DiagnosticsViewModel(AppServices services, PlaybackViewModel playback)
    {
        _outputs = services.Outputs;
        _probe = services.Probe;
        _loop = services.PlaybackLoop;
        _player = services.Playback;
        _audio = services.Audio;
        _playback = playback;
        var log = services.Log;
        foreach (var entry in log.Snapshot().Reverse()) LogEntries.Add(entry);
        log.EntryAdded += entry => Dispatcher.UIThread.Post(() => AddLogEntry(entry));
    }

    public ObservableCollection<OutputTimingRow> Outputs { get; } = new();

    /// <summary>Newest first.</summary>
    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    [ObservableProperty]
    public partial string UiResponsiveness { get; set; } = "";

    [ObservableProperty]
    public partial string Memory { get; set; } = "";

    [ObservableProperty]
    public partial string PlaybackState { get; set; } = "";

    [ObservableProperty]
    public partial string PlaybackTiming { get; set; } = "";

    [ObservableProperty]
    public partial string? PlaybackErrors { get; set; }

    [ObservableProperty]
    public partial string AudioState { get; set; } = "";

    [ObservableProperty]
    public partial bool AudioFaulted { get; set; }

    [ObservableProperty]
    public partial string TimelineIssues { get; set; } = "";

    public void Tick()
    {
        Outputs.Clear();
        foreach (var service in _outputs.Services)
        {
            var t = service.Timing;
            Outputs.Add(new OutputTimingRow(
                service.Universe.ToString(),
                service.Device.ToString(),
                service.Status.ToString(),
                $"{t.FramesPerSecond:0.0}/s",
                $"{t.IntervalP50Ms:0.0} / {t.IntervalP95Ms:0.0} / {t.IntervalMaxMs:0.0} ms",
                t.FramesSent.ToString("N0"),
                service.LastError ?? ""));
        }

        TickPlayback();

        var ui = _probe.Snapshot();
        UiResponsiveness = ui.Samples == 0
            ? "Measuring…"
            : $"UI thread wait: p50 {ui.P50Ms:0.0} ms · p95 {ui.P95Ms:0.0} ms · max {ui.MaxMs:0.0} ms (last {ui.Samples / 10} s)";

        using var process = Process.GetCurrentProcess();
        Memory = $"Working set {process.WorkingSet64 / 1048576.0:0} MB · managed heap {GC.GetTotalMemory(false) / 1048576.0:0.0} MB · " +
                 $"GCs gen0 {GC.CollectionCount(0)} / gen1 {GC.CollectionCount(1)} / gen2 {GC.CollectionCount(2)}";
    }

    void TickPlayback()
    {
        var focused = _playback.Timeline;
        var status = focused is null ? default : _player.StatusOf(focused.Id);
        var compiled = (focused is null ? null : _player.TimelineOf(focused.Id)) ?? CompiledTimeline.Empty;
        int running = _player.Running.Count;
        PlaybackState = $"{_playback.StateLabel} · {PlaybackViewModel.Format(status.Position)} / {PlaybackViewModel.Format(status.Length)} · " +
                        $"{focused?.Name ?? "no timeline"} ({compiled.LevelStepCount} level steps, {compiled.AudioCues.Count} sounds)" +
                        $" · {running} playing";
        var t = _loop.Timing;
        PlaybackTiming = $"Updates {t.FramesPerSecond:0.0}/s (target {1 / PlaybackLoop.Interval.TotalSeconds:0}) · " +
                         $"interval p50 {t.IntervalP50Ms:0.0} / p95 {t.IntervalP95Ms:0.0} / max {t.IntervalMaxMs:0.0} ms · " +
                         $"longest update {_loop.TakeLongestTick().TotalMilliseconds:0.00} ms";
        PlaybackErrors = _loop.ErrorCount == 0 ? null : $"{_loop.ErrorCount} failed updates; last: {_loop.LastError}";

        var audio = _audio.Status;
        AudioFaulted = audio.State != AudioDeviceState.Open;
        AudioState = audio.State switch
        {
            AudioDeviceState.Open => $"Audio: {audio.DeviceName} · {audio.Playing} playing · master {_audio.MasterVolume:P0}",
            _ => $"Audio: {audio.State}{(audio.LastError is { } e ? $": {e}" : "")}",
        };

        int errors = compiled.Issues.Count(i => i.Severity == Core.Timeline.TimelineIssueSeverity.Error);
        TimelineIssues = compiled.Issues.Count == 0 ? "Timeline: no problems"
            : $"Timeline: {errors} events skipped, {compiled.Issues.Count - errors} warnings (see the Timeline screen)";
    }

    void AddLogEntry(LogEntry entry)
    {
        LogEntries.Insert(0, entry);
        while (LogEntries.Count > MaxLogEntries) LogEntries.RemoveAt(LogEntries.Count - 1);
    }
}
