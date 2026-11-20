using System.Diagnostics;
using GhostShow.Audio;
using GhostShow.Core.Diagnostics;
using GhostShow.Core.Playback;
using GhostShow.Core.Timeline;
using GhostShow.Dmx;

namespace GhostShow.Server;

/// <summary>
/// The show computer measuring itself, for the Diagnostics screen (here and on devices, see <see cref="IDiagnostics"/>):
/// output timing, playback and audio, UI responsiveness, memory, and recent log messages. Refreshed once a second while
/// a Diagnostics screen shows it. On the show's thread.
/// </summary>
public sealed class LocalDiagnostics : IDiagnostics
{
    const int MaxLogEntries = 200;

    readonly OutputManager _outputs;
    readonly ShowThreadProbe _probe;
    readonly PlaybackLoop _loop;
    readonly PlaybackManager _player;
    readonly AudioService _audio;
    readonly Func<ShowTimeline?> _focused;
    readonly HashSet<object> _watching = [];
    List<LogEntry> _log;

    /// <param name="focused">The timeline the transport works (the one open in the editor), if any.</param>
    public LocalDiagnostics(ShowServices services, Func<ShowTimeline?> focused)
    {
        _outputs = services.Outputs;
        _probe = services.Probe;
        _loop = services.PlaybackLoop;
        _player = services.Playback;
        _audio = services.Audio;
        _focused = focused;
        _log = [.. services.Log.Snapshot().Reverse().Take(MaxLogEntries)];
        services.Log.EntryAdded += entry => services.Thread.Post(() => AddLogEntry(entry));
        Report = DiagnosticsReport.Empty with { Log = _log };
    }

    public DiagnosticsReport Report { get; private set; }

    public event Action? Changed;

    /// <summary>A device's Diagnostics screen opened or closed: while one is, the report is refreshed for it.</summary>
    public void SetWatching(object screen, bool watching)
    {
        if (watching) _watching.Add(screen);
        else _watching.Remove(screen);
        if (watching) Refresh();
    }

    /// <summary>A device shows Diagnostics (the show computer's own refreshes it while it's on screen).</summary>
    public bool IsWatched => _watching.Count > 0;

    /// <summary>Measures again: when the screen opens, then once a second while it's shown here or on a device.</summary>
    public void Refresh()
    {
        var outputs = _outputs.Services.Select(service =>
        {
            var t = service.Timing;
            return new OutputTimingRow(service.Universe.ToString(), service.Device.ToString(), service.Status.ToString(), $"{t.FramesPerSecond:0.0}/s",
                                       $"{t.IntervalP50Ms:0.0} / {t.IntervalP95Ms:0.0} / {t.IntervalMaxMs:0.0} ms", t.FramesSent.ToString("N0"),
                                       service.LastError ?? "");
        }).ToList();

        var focused = _focused();
        var status = focused is null ? default : _player.StatusOf(focused.Id);
        var compiled = (focused is null ? null : _player.TimelineOf(focused.Id)) ?? CompiledTimeline.Empty;
        string playbackState = $"{(focused is null ? "Stopped" : PlaybackText.StateLabel(status))} · {PlaybackText.Format(status.Position)} / {PlaybackText.Format(status.Length)} · " +
                               $"{focused?.Name ?? "no timeline"} ({compiled.LevelStepCount} level steps, {compiled.AudioCues.Count} sounds)" +
                               $" · {_player.Running.Count} playing";
        var t = _loop.Timing;
        string playbackTiming = $"Updates {t.FramesPerSecond:0.0}/s (target {1 / PlaybackLoop.Interval.TotalSeconds:0}) · " +
                                $"interval p50 {t.IntervalP50Ms:0.0} / p95 {t.IntervalP95Ms:0.0} / max {t.IntervalMaxMs:0.0} ms · " +
                                $"longest update {_loop.TakeLongestTick().TotalMilliseconds:0.00} ms";
        string? playbackErrors = _loop.ErrorCount == 0 ? null : $"{_loop.ErrorCount} failed updates; last: {_loop.LastError}";

        var audio = _audio.Status;
        string audioState = audio.State switch
        {
            AudioDeviceState.Open => $"Audio: {audio.DeviceName} · {audio.Playing} playing · master {_audio.MasterVolume:P0}",
            _ => $"Audio: {audio.State}{(audio.LastError is { } e ? $": {e}" : "")}",
        };

        int errors = compiled.Issues.Count(i => i.Severity == TimelineIssueSeverity.Error);
        string timelineIssues = compiled.Issues.Count == 0 ? "Timeline: no problems"
            : $"Timeline: {errors} events skipped, {compiled.Issues.Count - errors} warnings (see the Timeline screen)";

        var ui = _probe.Snapshot();
        string uiResponsiveness = ui.Samples == 0
            ? "Measuring…"
            : $"UI thread wait: p50 {ui.P50Ms:0.0} ms · p95 {ui.P95Ms:0.0} ms · max {ui.MaxMs:0.0} ms (last {ui.Samples / 10} s)";

        using var process = Process.GetCurrentProcess();
        string memory = $"Working set {process.WorkingSet64 / 1048576.0:0} MB · managed heap {GC.GetTotalMemory(false) / 1048576.0:0.0} MB · " +
                        $"GCs gen0 {GC.CollectionCount(0)} / gen1 {GC.CollectionCount(1)} / gen2 {GC.CollectionCount(2)}";

        Show(new DiagnosticsReport(outputs, playbackState, playbackTiming, playbackErrors, audioState, audio.State != AudioDeviceState.Open,
                                   timelineIssues, uiResponsiveness, memory, _log));
    }

    void AddLogEntry(LogEntry entry)
    {
        _log = [entry, .. _log.Take(MaxLogEntries - 1)];
        Show(Report with { Log = _log });
    }

    void Show(DiagnosticsReport report)
    {
        if (report.SameAs(Report)) return;
        Report = report;
        Changed?.Invoke();
    }
}
