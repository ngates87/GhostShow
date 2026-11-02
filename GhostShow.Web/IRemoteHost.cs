using GhostShow.Core.Fixtures;
using GhostShow.Core.Engine;
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

    /// <summary>Whether the show has changes not saved yet. Safe from any thread.</summary>
    bool HasUnsavedChanges { get; }

    /// <summary>
    /// Whether the show can't save itself yet: it has no show folder, and only the show computer can choose one
    /// (File → Save). Until then its changes stay unsaved. Safe from any thread.
    /// </summary>
    bool NeedsFirstSave { get; }

    /// <summary>The fixture library on the show computer: for a device's type picker, and custom fixture types.</summary>
    IFixtureLibrary Library { get; }

    /// <summary>The show computer's audio output devices, by name (for audio tracks' outputs). On the show's thread.</summary>
    IReadOnlyList<string> AudioDevices { get; }

    /// <summary>The list of audio devices changed (a USB interface plugged in). Raised on the show's thread.</summary>
    event Action? AudioDevicesChanged;
}
