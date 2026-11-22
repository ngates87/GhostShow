using GhostShow.Core.Diagnostics;
using GhostShow.Core.Dmx;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Inputs;
using GhostShow.Core.Playback;
using GhostShow.Web;

namespace GhostShow.Server;

/// <summary>
/// What the web remote serves devices from: the server's own show, playback, commands, files and setup, with the show
/// edited on the show's thread (<see cref="ShowServices.Thread"/>). Needs no window: the desktop app passes the same
/// pieces its screens use while it hosts the server.
/// </summary>
public sealed class ShowRemoteHost(ShowServices services, ShowPlayback playback, ShowControl control, ShowFiles files, ServerTick tick,
                                   AudioOutputs audioOutputs, IFixtureLibrary library, IOutputSetup outputs, ILiveLevels levels,
                                   ShowTriggers triggers, LocalInputSetup inputs, LocalAudioSetup audio, LocalDiagnostics diagnostics,
                                   ShowLoad load) : IRemoteHost
{
    public Task<T> InvokeAsync<T>(Func<T> function) => services.Thread.InvokeAsync(function);
    public ShowSession Session => services.Session;
    public ShowPlayback Playback => playback;
    public Guid? FocusedTimeline => control.FocusedTimeline;
    public IShowControl Control => control;
    public IReadOnlyList<string> Warnings => tick.Warnings;
    public bool HasUnsavedChanges => files.IsDirty;
    public bool NeedsFirstSave => files.NeedsFirstSave;
    public IFixtureLibrary Library => library;
    public IOutputSetup Outputs => outputs;
    public ILiveLevels Levels => levels;
    public ITriggerControl Triggers => triggers;
    public IInputSetup Inputs => inputs;
    public IAudioSetup Audio => audio;
    public void WatchAudio(object device, bool watching) => audio.SetWatching(device, watching);
    public IDiagnostics Diagnostics => diagnostics;
    public LoadReading Load => load.Reading;
    public void WatchDiagnostics(object device, bool watching) => diagnostics.SetWatching(device, watching);
    public IReadOnlyList<string> AudioDevices => audioOutputs.Names;

    public event Action? AudioDevicesChanged
    {
        add => audioOutputs.Changed += value;
        remove => audioOutputs.Changed -= value;
    }
}
