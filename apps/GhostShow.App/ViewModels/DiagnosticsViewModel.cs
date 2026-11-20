using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GhostShow.Core.Diagnostics;

namespace GhostShow.App.ViewModels;

/// <summary>
/// Output timing, playback and audio, UI responsiveness, memory, and recent log messages: the show computer's
/// (<see cref="IDiagnostics"/>), on its screen and a device's alike. Refreshed once a second while it's shown.
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    readonly IDiagnostics _diagnostics;

    public DiagnosticsViewModel(IDiagnostics diagnostics)
    {
        _diagnostics = diagnostics;
        diagnostics.Changed += Show;
        Show();
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

    void Show()
    {
        var report = _diagnostics.Report;
        if (!report.Outputs.SequenceEqual(Outputs))
        {
            Outputs.Clear();
            foreach (var row in report.Outputs) Outputs.Add(row);
        }
        PlaybackState = report.PlaybackState;
        PlaybackTiming = report.PlaybackTiming;
        PlaybackErrors = report.PlaybackErrors;
        AudioState = report.AudioState;
        AudioFaulted = report.AudioFaulted;
        TimelineIssues = report.TimelineIssues;
        UiResponsiveness = report.UiResponsiveness;
        Memory = report.Memory;
        ShowLog(report.Log);
    }

    /// <summary>New entries go in at the top, so the list doesn't jump back to the start each time.</summary>
    void ShowLog(IReadOnlyList<LogEntry> log)
    {
        if (log.SequenceEqual(LogEntries)) return;
        int added = LogEntries.Count == 0 ? -1 : log.ToList().IndexOf(LogEntries[0]);
        if (added > 0 && log.Skip(added).Take(LogEntries.Count).SequenceEqual(LogEntries.Take(log.Count - added)))
        {
            for (int i = added - 1; i >= 0; i--) LogEntries.Insert(0, log[i]);
            while (LogEntries.Count > log.Count) LogEntries.RemoveAt(LogEntries.Count - 1);
            return;
        }
        LogEntries.Clear();
        foreach (var entry in log) LogEntries.Add(entry);
    }
}
