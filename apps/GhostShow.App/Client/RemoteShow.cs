using System.Text.Json.Nodes;
using Avalonia.Threading;
using GhostShow.App.ViewModels;
using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Inputs;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhostShow.App.Client;

/// <summary>
/// What a device's main view runs on: a copy of the show kept in step with the show computer's over the connection
/// (<see cref="RemoteLink"/>), and the services the screens use over it (<see cref="Services"/>). The browser's page and
/// the desktop window over the connection both build their <see cref="ShellViewModel"/> on one. Every message is
/// handled on the UI thread; what only the host knows (its screens, its title, who it's paired as) it does on the events
/// below. See docs/remote.md for the messages.
/// </summary>
sealed class RemoteShow
{
    public RemoteShow(RemoteEndpoint endpoint, IReadOnlyList<FixtureProfile> builtIn)
    {
        Session = new ShowSession(new ShowEngine(), new FixtureProfileLibrary(builtIn), NullLogger<ShowSession>.Instance);
        Link = new RemoteLink(endpoint);
        Playback = new RemotePlayback(Link);
        AudioDevices = new AudioDeviceList((device, side) =>   // the tone plays on the show computer
        {
            Link.Send("testSound", ("device", device), ("side", side.ToString()));
            return true;
        });
        Edits = new RemoteEdits(Session.Document, Link);
        Library = new RemoteFixtureLibrary(Session, Link, builtIn);
        Outputs = new RemoteOutputSetup(Link);
        Levels = new RemoteLiveLevels(Session, Link);
        Triggers = new RemoteTriggers(Link);
        Inputs = new RemoteInputSetup(Link);
        Audio = new RemoteAudioSetup(Link);
        Diagnostics = new RemoteDiagnostics(Link);
        Services = new ShellServices(Playback, Library, Outputs, Levels, AudioDevices, Triggers, Edits);

        // Messages arrive on the connection's thread; each is handled on the UI thread.
        static void OnUi(Action action) => Dispatcher.UIThread.Post(action);
        Link.Hello += m => OnUi(() => OnHello(m));
        Link.Changed += (description, ops) => OnUi(() => OnChange(description, ops));
        Link.Status += m => OnUi(() => OnStatus(m));
        Link.Issues += m => OnUi(() => Playback.OnIssues(m));
        Link.Patch += m => OnUi(() => OnPatch(m));
        Link.Manual += m => OnUi(() => OnManualLevels(m));
        Link.Audio += m => OnUi(() => Playback.OnAudio(m));
        Link.AudioDevices += m => OnUi(() => AudioDevices.Update(Names(m["devices"] as JsonArray)));
        Link.Result += m => OnUi(() => Edits.OnResult(m));
        Link.History += m => OnUi(() => Edits.OnHistory(m));
        Link.Library += m => OnUi(() => Library.OnStatus(m));
        Link.Outputs += m => OnUi(() => Outputs.OnOutputs(m));
        Link.Levels += m => OnUi(() => Levels.OnLevels(m));
        Link.Device += m => OnUi(() => DeviceChanged?.Invoke(m["device"]));
        Link.Triggers += m => OnUi(() => TriggersChanged?.Invoke(RemoteTriggers.Read(m)));
        Link.Diagnostics += m => OnUi(() => Diagnostics.OnReport(m));
        Link.AudioSetup += m => OnUi(() => Audio.OnAudio(m));
        Link.Inputs += m => OnUi(() => Inputs.OnInputs(m));
        Link.Input += m => OnUi(() => InputArrived?.Invoke(m));
        Link.Disconnected += () => OnUi(() => Disconnected?.Invoke());
        Link.Problem += m => OnUi(() => Problem?.Invoke(m));
    }

    public ShowSession Session { get; }
    public RemoteLink Link { get; }
    public RemotePlayback Playback { get; }
    public AudioDeviceList AudioDevices { get; }
    public RemoteEdits Edits { get; }
    public RemoteFixtureLibrary Library { get; }
    public RemoteOutputSetup Outputs { get; }
    public RemoteLiveLevels Levels { get; }
    public RemoteTriggers Triggers { get; }
    public RemoteInputSetup Inputs { get; }
    public RemoteAudioSetup Audio { get; }
    public RemoteDiagnostics Diagnostics { get; }

    /// <summary>The services a <see cref="ShellViewModel"/> is built on, over the connection.</summary>
    public ShellServices Services { get; }

    /// <summary>The whole show is about to replace this copy (the old one is still here: remember what's open on it).</summary>
    public event Action? Arriving;

    /// <summary>The whole show arrived (on connecting, reconnecting, or another show was opened): the screens show it. The "hello" message.</summary>
    public event Action<JsonObject>? Arrived;

    /// <summary>A "status" message: where everything is, and the show computer's warnings, unsaved changes and load.</summary>
    public event Action<JsonObject>? StatusArrived;

    /// <summary>Who this device is paired as changed on the show computer.</summary>
    public event Action<JsonNode?>? DeviceChanged;

    /// <summary>How the show computer's triggers stand.</summary>
    public event Action<TriggerStates>? TriggersChanged;

    /// <summary>An input arrived on the show computer (for the input monitor, or a trigger being learned).</summary>
    public event Action<JsonObject>? InputArrived;

    /// <summary>The connection dropped (it's being tried again).</summary>
    public event Action? Disconnected;

    /// <summary>Why it isn't connected (until the show arrives).</summary>
    public event Action<string>? Problem;

    /// <summary>Connects (and reconnects, whenever the connection drops) until <paramref name="stop"/>.</summary>
    public Task RunAsync(CancellationToken stop = default) => Link.RunAsync(stop);

    static string[] Names(JsonArray? devices) => [.. (devices ?? []).Select(d => d!.GetValue<string>())];

    void OnHello(JsonObject message)
    {
        Arriving?.Invoke();
        Edits.OnHello();
        Playback.Reset();
        Library.UseProfilesOfTheShow(message["profiles"] as JsonArray);
        if (message["library"] is JsonObject library) Library.OnHello(library);
        Session.Use(ShowDocument.FromSnapshot(message["show"]!));
        Outputs.OnHello();
        Levels.OnHello();
        Inputs.OnHello();
        Audio.OnHello();
        Diagnostics.OnHello();
        AudioDevices.Update(Names(message["audioDevices"] as JsonArray));
        Arrived?.Invoke(message);
    }

    /// <summary>A change made on the show computer (or another device): applied to this copy, the same way.</summary>
    void OnChange(string description, IReadOnlyList<EditOp> ops)
    {
        Edits.OnRemoteChange();
        try
        {
            Session.Document.Apply(new Transaction(description, ops), origin: "show computer");   // an origin: it isn't sent back
        }
        catch (EditException)
        {
            Link.Resync();   // out of step: get the whole show again
        }
    }

    void OnStatus(JsonObject message)
    {
        Playback.OnStatus(message);
        StatusArrived?.Invoke(message);
    }

    /// <summary>
    /// The patch changed on the show computer. The fixtures and universes themselves arrive as edits, like every other
    /// change; this brings the profiles they use, so channels are named alike.
    /// </summary>
    void OnPatch(JsonObject message)
    {
        Library.UseProfilesOfTheShow(message["profiles"] as JsonArray);
        Session.Apply();   // the screens follow (track headers name channels by fixture)
    }

    /// <summary>
    /// Levels set by hand on the show computer, copied into this copy's engine: Snapshot (handles at the current
    /// levels) reads them here as it does on the show computer.
    /// </summary>
    void OnManualLevels(JsonObject message)
    {
        int universe = message["universe"]!.GetValue<int>();
        var levels = Convert.FromBase64String(message["levels"]!.GetValue<string>());
        for (int address = 1; address <= levels.Length; address++) Session.Engine.SetChannel(universe, address, levels[address - 1]);
    }
}
