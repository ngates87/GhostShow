using GhostShow.Audio;
using GhostShow.Core.Diagnostics;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Playback;
using GhostShow.Core.Storage;
using GhostShow.Dmx;
using GhostShow.Dmx.Discovery;
using GhostShow.Inputs;
using Microsoft.Extensions.Logging;

namespace GhostShow.Server;

/// <summary>
/// The long-lived services the show computer runs on, created once at startup (<see cref="StartAsync"/>) and handed to
/// the screens. None of them depend on a UI; the same set runs headless in the CLI.
/// </summary>
/// <param name="Thread">The thread the show is edited on (the desktop's UI thread, while it hosts the server).</param>
/// <param name="Probe">How quickly that thread responds.</param>
public sealed record ShowServices(
    ShowSession Session,
    OutputManager Outputs,
    PlaybackManager Playback,
    PlaybackLoop PlaybackLoop,
    AudioService Audio,
    MachineSettings Settings,
    AppPaths Paths,
    RecentEventsLog Log,
    ShowThreadProbe Probe,
    IShowThread Thread,
    IDmxAdapterFinder? AdapterFinder = null,
    SerialInputs? SerialInputs = null)
{
    /// <summary>
    /// Builds the services: machine settings, the engine and show session with the built-in fixture profiles (the full
    /// library loads later, in the background), DMX outputs, audio (its device opens in the background), playback on
    /// its own thread, and the serial inputs. Returns them, and why the settings couldn't be read, if they couldn't.
    /// </summary>
    public static async Task<(ShowServices Services, string? SettingsError)> StartAsync(ILoggerFactory loggers, AppPaths paths, RecentEventsLog log,
                                                                                      IShowThread thread)
    {
        var settings = await MachineSettingsStore.LoadAsync(paths);
        var builtInProfiles = new FixtureProfileLibrary(FixtureProfileLoader.LoadBuiltIn());
        var engine = new ShowEngine();
        var session = new ShowSession(engine, builtInProfiles, loggers.CreateLogger<ShowSession>());
        var outputs = new OutputManager(engine, loggers);

        var audio = new AudioService(loggers.CreateLogger<AudioService>()) { MasterVolume = settings.Settings.Audio.MasterVolume };
        _ = audio.OpenAsync(settings.Settings.Audio.DeviceName);
        var player = new PlaybackManager(engine, audio, new StopwatchClock());
        var playbackLoop = new PlaybackLoop(player, loggers.CreateLogger<PlaybackLoop>());
        playbackLoop.Start();

        var serialInputs = new SerialInputs(loggers.CreateLogger<SerialInputs>());
        return (new ShowServices(session, outputs, player, playbackLoop, audio, settings.Settings, paths, log, new ShowThreadProbe(thread), thread,
                                 SerialInputs: serialInputs), settings.Error);
    }
}
