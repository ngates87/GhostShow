using GhostShow.Core.Dmx;
using GhostShow.Core.Playback;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;
using GhostShow.Dmx;
using GhostShow.Inputs;
using Microsoft.Extensions.Logging;

namespace GhostShow.Server;

/// <summary>
/// The show computer's server: everything that runs the show, built from its services (<see cref="ShowServices"/>) and
/// connected up, whether or not a window is open. Playback, the triggers, the show's files, what devices can ask
/// (<see cref="Control"/>), the DMX and serial setup, sound, diagnostics, Remote Access and the server's own refresh
/// (<see cref="Tick"/>). Everything here runs on the show's thread (<see cref="ShowServices.Thread"/>); what arrives on
/// other threads (serial commands, a sound measured) is posted to it.
/// </summary>
/// <remarks>
/// A window adds its screens on top (the desktop app's MainViewModel), telling the server the few things only it knows:
/// the timeline open in its editor (<see cref="FocusedTimeline"/>), and how its screens follow another show
/// (<see cref="ScreensFollowShow"/>). A host with no window ticks the server itself (see <see cref="StartAsync"/>).
/// </remarks>
public sealed class ShowServer
{
    readonly ShowServices _services;

    public ShowServer(ShowServices services, ILogger logger)
    {
        _services = services;
        var (session, settings, paths, thread) = (services.Session, services.Settings, services.Paths, services.Thread);

        Playback = new ShowPlayback(session, services.Playback, services.Audio, logger);
        // A sound's length was measured (on a worker thread): timelines using it are compiled again, on the show's thread.
        services.Audio.LengthMeasured += path => thread.Post(() => Playback.OnAudioMeasured(path));
        Outputs = new LocalOutputSetup(session, services.Outputs, settings, SaveSettingsAsync, services.AdapterFinder);
        Levels = new LocalLiveLevels(session.Engine, services.Outputs.Find);
        Library = new LocalFixtureLibrary(session, paths, settings, logger);
        Triggers = new ShowTriggers(session, Playback, Blackout);
        AudioOutputs = new AudioOutputs(services.Audio);
        Files = new ShowFiles(session, settings, paths, SaveSettingsAsync, thread);
        Control = new ShowControl(session, Playback, Triggers, Files, () => FocusedTimeline(),
                                  () => { Blackout(); DeviceBlackout?.Invoke(); }, AudioOutputs.PlayTestSound);
        Diagnostics = new LocalDiagnostics(services, () => FocusedTimeline() is { } id ? session.Project.Timelines.FirstOrDefault(t => t.Id == id) : null);
        Load = new ShowLoad(services);

        Serial = services.SerialInputs ?? new SerialInputs(logger);
        Inputs = new LocalInputSetup(session, Serial, settings, SaveSettingsAsync,
            // Only adapters that have their port open: one that's missing keeps the name of the port it was last on, and
            // another device may have that name now (the DMX output checks the adapter's serial number, so it won't take it).
            () => services.Outputs.Services.Where(s => s.Status == DmxOutputStatus.Connected && s.Device.Port is not null).Select(s => (s.Universe, s.Device.Port!)));
        Inputs.Changed += () => Triggers.SerialListening = Inputs.State.Devices.Any(d => d.IsListening);
        // Both come on a port's reading thread. A command is an input like a key.
        Serial.CommandReceived += (device, command) => thread.Post(() =>
        {
            Inputs.OnCommand(device, command, DateTimeOffset.Now);   // shown on its card
            Triggers.Command(device, command);
        });
        Serial.StatusChanged += _ => thread.Post(Inputs.Refresh);

        Audio = new LocalAudioSetup(services.Audio, session, settings, SaveSettingsAsync, thread);
        Tick = new ServerTick(services, Triggers, Load, Outputs, Inputs, Diagnostics, Audio);
        Remote = new ShowRemote(new ShowRemoteHost(services, Playback, Control, Files, Tick, AudioOutputs, Library, Outputs, Levels,
                                                   Triggers, Inputs, Audio, Diagnostics, Load),
                                settings.Remote, SaveSettingsAsync, thread, logger, ShowRemote.FindBrowserApp());

        session.Document.Changed += _ => Files.MarkDirty();   // any edit, from a screen here or a device
        Files.Renamed += Remote.ShowReplaced;   // devices show the new name
        Files.AfterShowReplaced = AfterShowReplacedAsync;
    }

    public ShowServices Services => _services;
    public ShowPlayback Playback { get; }
    public LocalOutputSetup Outputs { get; }
    public LocalLiveLevels Levels { get; }
    public LocalFixtureLibrary Library { get; }
    public ShowTriggers Triggers { get; }
    public AudioOutputs AudioOutputs { get; }
    public ShowFiles Files { get; }
    public ShowControl Control { get; }
    public LocalDiagnostics Diagnostics { get; }
    public ShowLoad Load { get; }
    public SerialInputs Serial { get; }
    public LocalInputSetup Inputs { get; }
    public LocalAudioSetup Audio { get; }
    public ServerTick Tick { get; }
    public ShowRemote Remote { get; }

    /// <summary>The timeline open in the window's editor (the transport works it), if any. Read from any thread.</summary>
    public Func<Guid?> FocusedTimeline { get; set; } = () => null;

    /// <summary>Another show is open: the window's screens follow it (after the triggers and inputs have, before devices are told).</summary>
    public Func<Task> ScreensFollowShow { get; set; } = () => Task.CompletedTask;

    /// <summary>A device asked for Blackout (after it's done).</summary>
    public event Action? DeviceBlackout;

    /// <summary>This computer's settings couldn't be saved: why.</summary>
    public event Action<string>? Problem;

    /// <summary>Stops playback and sets every manual level to 0 (Blackout, or a button's Blackout action).</summary>
    public void Blackout()
    {
        _services.Playback.StopAllNow();   // otherwise the timelines would put their levels straight back
        _services.Session.Engine.Blackout();
    }

    /// <summary>Saves this computer's settings. A problem is reported (<see cref="Problem"/>).</summary>
    public async Task SaveSettingsAsync()
    {
        try
        {
            await MachineSettingsStore.SaveAsync(_services.Settings, _services.Paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem?.Invoke($"Couldn't save machine settings: {ex.Message}");
        }
    }

    async Task AfterShowReplacedAsync()
    {
        Playback.Reset();   // the old show's timelines stop at once
        Triggers.Reload();   // every trigger ready, and armed
        Inputs.Reload();   // the new show's input devices, on this computer's ports
        await ScreensFollowShow();
        Remote.ShowReplaced();
        await Outputs.ApplyAsync();
    }

    /// <summary>
    /// Starts output and the serial inputs, then the refresh (<paramref name="refreshing"/>: a window's timer, or
    /// <see cref="ServerTick.Start"/>), reopens the show used last (if it's still there), starts Remote Access if it was
    /// left on, and loads the full fixture library.
    /// </summary>
    public async Task StartAsync(Action refreshing)
    {
        await Outputs.ApplyAsync();
        Inputs.RefreshPorts();
        Inputs.Apply();   // after the DMX outputs, so a port they use isn't taken
        refreshing();
        if (Files.RecentShows.FirstOrDefault() is { Exists: true } last) await Files.OpenAsync(last.Path);
        await Remote.StartIfEnabledAsync();
        await Library.LoadAsync();
    }

    /// <summary>
    /// Stops: devices are told it's going, then the show stops (audio stops, timelines release the lights), the playback
    /// thread, audio and serial inputs, and last the DMX outputs, which send their blackout. False if an output didn't
    /// stop in time.
    /// </summary>
    public async Task<bool> ShutdownAsync()
    {
        Tick.Dispose();
        await Remote.DisposeAsync();
        _services.Playback.StopAllNow();
        await Task.Delay(PlaybackLoop.Interval * 3);
        _services.PlaybackLoop.Stop(TimeSpan.FromSeconds(2));
        _services.Audio.Dispose();
        Serial.Dispose();
        return await _services.Outputs.StopAllAsync();
    }
}
