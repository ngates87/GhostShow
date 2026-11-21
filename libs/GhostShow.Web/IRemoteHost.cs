using GhostShow.Core.Diagnostics;
using GhostShow.Core.Dmx;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Engine;
using GhostShow.Core.Inputs;
using GhostShow.Core.Playback;

namespace GhostShow.Web;

/// <summary>What the web remote needs from the app that hosts it (the desktop app).</summary>
public interface IRemoteHost
{
    /// <summary>
    /// Runs a function on the thread the show is edited on (the UI's) and returns what it returns. Everything that
    /// reads or changes <see cref="Document"/> goes through this.
    /// </summary>
    Task<T> InvokeAsync<T>(Func<T> function);

    /// <summary>
    /// The open show: its document (<see cref="ShowSession.Document"/>), patch and fixture profiles. Only touched on the
    /// show's thread (see <see cref="InvokeAsync"/>), and its events come on it; except its engine's levels, which are
    /// safe to read from any thread.
    /// </summary>
    ShowSession Session { get; }

    /// <summary>
    /// Playback on the show computer. Its status (<see cref="ShowPlayback.StatusOf"/>, <see cref="ShowPlayback.Running"/>)
    /// is safe to read from any thread; everything else on the show's thread only.
    /// </summary>
    ShowPlayback Playback { get; }

    /// <summary>The timeline open in the editor (the transport's), if any. Safe from any thread.</summary>
    Guid? FocusedTimeline { get; }

    /// <summary>What devices can ask the show computer to do. Called on the show's thread only.</summary>
    IShowControl Control { get; }

    /// <summary>Problems worth knowing about (the desktop's ⚠ list: patch errors, failing outputs). Safe from any thread.</summary>
    IReadOnlyList<string> Warnings { get; }

    /// <summary>How hard the show computer is working, for every device's load meter. Read on any thread.</summary>
    LoadReading Load => LoadReading.Measuring;

    /// <summary>Whether the show has changes not saved yet. Safe from any thread.</summary>
    bool HasUnsavedChanges { get; }

    /// <summary>
    /// Whether the show can't save itself yet: it has no show folder, and only the show computer can choose one
    /// (File → Save). Until then its changes stay unsaved. Safe from any thread.
    /// </summary>
    bool NeedsFirstSave { get; }

    /// <summary>The fixture library on the show computer: for a device's type picker, and custom fixture types.</summary>
    IFixtureLibrary Library { get; }

    /// <summary>The DMX hardware on the show computer: adapters found, and which device each universe uses. On the show's thread.</summary>
    IOutputSetup Outputs { get; }

    /// <summary>The levels the lights are at, and setting them by hand: for a device's faders and monitor. On the show's thread.</summary>
    ILiveLevels Levels { get; }

    /// <summary>The show's triggers, which run only on the show computer: how they stand, arming, learning. On the show's thread.</summary>
    ITriggerControl Triggers { get; }

    /// <summary>The show computer's serial ports, for the Inputs screen: which port each input device is on there. On the show's thread.</summary>
    IInputSetup Inputs { get; }

    /// <summary>The show computer's sound, for the Audio screen: its default output, master volume and the show's sound files. On the show's thread.</summary>
    IAudioSetup Audio { get; }

    /// <summary>A device's Audio screen opened or closed: while one is open, the show computer keeps <see cref="Audio"/> up to date. On the show's thread.</summary>
    void WatchAudio(object device, bool watching);

    /// <summary>How the show computer is running, for the Diagnostics screen. On the show's thread.</summary>
    IDiagnostics Diagnostics { get; }

    /// <summary>A device's Diagnostics screen opened or closed: while one is open, the show computer measures itself once a second. On the show's thread.</summary>
    void WatchDiagnostics(object device, bool watching);

    /// <summary>The show computer's audio output devices, by name (for audio tracks' outputs). On the show's thread.</summary>
    IReadOnlyList<string> AudioDevices { get; }

    /// <summary>The list of audio devices changed (a USB interface plugged in). Raised on the show's thread.</summary>
    event Action? AudioDevicesChanged;
}
