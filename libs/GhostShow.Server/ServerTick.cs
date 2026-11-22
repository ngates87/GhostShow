using GhostShow.Core.Timeline;
using GhostShow.Dmx;

namespace GhostShow.Server;

/// <summary>
/// The server's own refresh, on the show's thread, whether or not any screen is open: triggers re-arm and fire again
/// while held (every tick); the load meter is measured twice a second; and once a second the DMX outputs and serial
/// inputs are checked, a device's Diagnostics or Audio screen is kept up to date, the sound devices audio tracks use
/// are opened before they're needed, and the warnings are collected.
/// </summary>
/// <remarks>
/// While the desktop app hosts the server, its refresh timer calls <see cref="Tick"/> (at background priority, so it
/// waits for input and drawing). A host with no window calls <see cref="Start"/> instead.
/// </remarks>
public sealed class ServerTick(ShowServices services, ShowTriggers triggers, ShowLoad load, LocalOutputSetup outputs,
                               LocalInputSetup inputs, LocalDiagnostics diagnostics, LocalAudioSetup audio) : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(33);

    /// <summary>Ticks in a second, at <see cref="Interval"/>.</summary>
    public const int PerSecond = 30;

    int _count;
    volatile string[] _warnings = [];
    ShowHeartbeat? _heartbeat;

    /// <summary>What needs attention (the ⚠ list), as of the last second. Safe from any thread.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Ticks so far.</summary>
    public int Count => _count;

    public void Tick()
    {
        triggers.Tick();
        _count++;
        if (_count % (PerSecond / 2) == 0) load.Measure();   // devices get it in the status
        if (_count % PerSecond != 0) return;
        outputs.Tick();
        inputs.Tick();
        if (diagnostics.IsWatched) diagnostics.Refresh();
        if (audio.IsWatched) audio.Refresh();
        services.Audio.UseOutputs(services.Session.Project.Timelines.SelectMany(t => t.Tracks)
            .Where(t => t.Kind == TrackKind.Audio && !string.IsNullOrWhiteSpace(t.AudioDevice)).Select(t => t.AudioDevice!));   // cheap when nothing changed
        _warnings = [.. ShowWarnings.Collect(services, inputs)];
    }

    /// <summary>Ticks by itself, for a host with no refresh timer. A tick that falls due while the last is still waiting is skipped.</summary>
    public void Start() => _heartbeat ??= new ShowHeartbeat(services.Thread, Interval, Tick);

    public void Dispose() => _heartbeat?.Dispose();
}
