namespace GhostShow.Core.Diagnostics;

/// <summary>One universe's output, as Diagnostics shows its timing.</summary>
public sealed record OutputTimingRow(string Universe, string Device, string Status, string Rate, string Intervals, string Frames, string LastError);

/// <summary>What the Diagnostics screen shows about the show computer, in words.</summary>
/// <param name="Log">Recent warnings and errors, newest first.</param>
public sealed record DiagnosticsReport(IReadOnlyList<OutputTimingRow> Outputs, string PlaybackState, string PlaybackTiming, string? PlaybackErrors,
                                       string AudioState, bool AudioFaulted, string TimelineIssues, string UiResponsiveness, string Memory,
                                       IReadOnlyList<LogEntry> Log)
{
    public static readonly DiagnosticsReport Empty = new([], "", "", null, "", false, "", "Measuring…", "", []);

    /// <summary>The same on screen? (A record's own comparison would only compare the lists by reference.)</summary>
    public bool SameAs(DiagnosticsReport other) =>
        this with { Outputs = other.Outputs, Log = other.Log } == other && Outputs.SequenceEqual(other.Outputs) && Log.SequenceEqual(other.Log);
}

/// <summary>
/// How the show computer is running, for the Diagnostics screen: output timing, playback, sound, its UI thread and
/// memory, and recent log messages. Only the show computer can measure itself (<c>LocalDiagnostics</c>); a device's
/// screen is sent the report (<c>RemoteDiagnostics</c> in GhostShow.Browser).
/// </summary>
public interface IDiagnostics
{
    DiagnosticsReport Report { get; }

    /// <summary><see cref="Report"/> changed. On the show's thread on the show computer; on the UI thread on a device.</summary>
    event Action? Changed;
}
