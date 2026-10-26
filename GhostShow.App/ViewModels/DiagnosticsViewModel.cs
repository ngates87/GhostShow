using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GhostShow.Core.Diagnostics;
using GhostShow.Dmx;

namespace GhostShow.App.ViewModels;

public sealed record OutputTimingRow(string Universe, string Device, string Status, string Rate, string Intervals, string Frames, string LastError);

/// <summary>Output timing, UI responsiveness, memory, and recent log messages. Refreshed once a second.</summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    const int MaxLogEntries = 200;

    readonly OutputManager _outputs;
    readonly UiResponsivenessProbe _probe;

    public DiagnosticsViewModel(OutputManager outputs, UiResponsivenessProbe probe, RecentEventsLog log)
    {
        _outputs = outputs;
        _probe = probe;
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

        var ui = _probe.Snapshot();
        UiResponsiveness = ui.Samples == 0
            ? "Measuring…"
            : $"UI thread wait: p50 {ui.P50Ms:0.0} ms · p95 {ui.P95Ms:0.0} ms · max {ui.MaxMs:0.0} ms (last {ui.Samples / 10} s)";

        using var process = Process.GetCurrentProcess();
        Memory = $"Working set {process.WorkingSet64 / 1048576.0:0} MB · managed heap {GC.GetTotalMemory(false) / 1048576.0:0.0} MB · " +
                 $"GCs gen0 {GC.CollectionCount(0)} / gen1 {GC.CollectionCount(1)} / gen2 {GC.CollectionCount(2)}";
    }

    void AddLogEntry(LogEntry entry)
    {
        LogEntries.Insert(0, entry);
        while (LogEntries.Count > MaxLogEntries) LogEntries.RemoveAt(LogEntries.Count - 1);
    }
}
