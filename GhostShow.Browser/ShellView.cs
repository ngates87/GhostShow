using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GhostShow.App.ViewModels;
using GhostShow.App.Views;
using GhostShow.Browser.Remote;
using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Model;
using GhostShow.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhostShow.Browser;

/// <summary>
/// The page: says what it's doing until the show has arrived, then shows the desktop app's own Timeline screen on a
/// copy of the show kept in step with the show computer's. Each kind of message from the show computer has its own
/// handler below (see docs/remote.md for the messages).
/// </summary>
sealed class ShellView : Border
{
    static readonly IReadOnlyList<FixtureProfile> BuiltInProfiles = FixtureProfileLoader.LoadBuiltIn();

    readonly TextBlock _message = new() { Foreground = Brushes.Gray, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new(24) };
    readonly ShowSession _session = new(new ShowEngine(), new FixtureProfileLibrary(BuiltInProfiles), NullLogger<ShowSession>.Instance);
    readonly RemoteLink _link = new();
    readonly RemotePlayback _remote;
    readonly PlaybackViewModel _playback;
    readonly AudioDeviceList _audioDevices;
    TimelineEditorViewModel? _editor;
    string _device = "";

    public ShellView()
    {
        Background = Brushes.Black;
        Child = _message;
        _message.Text = "Connecting to the show computer…";
        _remote = new RemotePlayback(_link);
        _playback = new PlaybackViewModel(_remote);   // the desktop's own, over the show computer's playback
        _audioDevices = new AudioDeviceList(_link);

        // Messages arrive on the connection's thread; each is handled on the UI thread.
        _link.Hello += m => Dispatcher.UIThread.Post(() => OnHello(m));
        _link.Changed += (description, ops) => Dispatcher.UIThread.Post(() => OnChange(description, ops));
        _link.Status += m => Dispatcher.UIThread.Post(() => OnStatus(m));
        _link.Issues += m => Dispatcher.UIThread.Post(() => _remote.OnIssues(m));
        _link.Patch += m => Dispatcher.UIThread.Post(() => OnPatch(m));
        _link.Manual += m => Dispatcher.UIThread.Post(() => OnManualLevels(m));
        _link.Audio += m => Dispatcher.UIThread.Post(() => _remote.OnAudio(m));
        _link.AudioDevices += m => Dispatcher.UIThread.Post(() => _audioDevices.Update(m["devices"] as JsonArray));
        _link.Problem += m => Dispatcher.UIThread.Post(() =>
        {
            if (_editor is null) _message.Text = m;
        });
    }

    /// <summary>Connects (and reconnects, whenever the connection drops) until the page is closed.</summary>
    public Task StartAsync() => _link.RunAsync();

    /// <summary>The whole show (on connecting, or another show was opened): a fresh copy, and the screens on it.</summary>
    void OnHello(JsonObject message)
    {
        _device = $"{message["device"]?["name"]} ({message["device"]?["role"]})";
        _playback.Reset();
        UseProfiles(message["profiles"] as JsonArray);
        _session.Use(ShowDocument.FromSnapshot(message["show"]!));
        _audioDevices.Update(message["audioDevices"] as JsonArray);
        if (_editor is null)
        {
            _editor = new TimelineEditorViewModel(_session, _playback, () => Task.FromResult<string?>(null), () => { }, _audioDevices);
            Child = new DockPanel
            {
                Children =
                {
                    new TextBlock { Text = $"{_device} · experimental browser build · edits here stay on this device", Foreground = Brushes.Gray, FontSize = 11, Margin = new(8, 4), [DockPanel.DockProperty] = Dock.Bottom },
                    new TimelineView { DataContext = _editor, Margin = new(6) },
                },
            };
        }
        else _editor.Reload();
        OpenFromAddress();
    }

    /// <summary>
    /// <c>/app/?open=Rooms/Lobby</c> opens that timeline (by its place in the tree) once the show has arrived: a
    /// bookmark for the timeline you're working on.
    /// </summary>
    void OpenFromAddress()
    {
        var query = System.Runtime.InteropServices.JavaScript.JSHost.GlobalThis.GetPropertyAsJSObject("location")?.GetPropertyAsString("search") ?? "";
        var open = query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == "open" && p.Length == 2)?[1];
        if (open is null || _editor is null) return;
        if (_session.Project.FindTimeline(Uri.UnescapeDataString(open)) is { } timeline) _editor.OpenTimeline(timeline);
    }

    /// <summary>A change made on the show computer (or another device): applied to this copy, the same way.</summary>
    void OnChange(string description, IReadOnlyList<EditOp> ops)
    {
        try
        {
            _session.Document.Apply(new Transaction(description, ops), origin: "show computer");
        }
        catch (EditException)
        {
            _link.Resync();   // out of step (an edit made here, say): get the whole show again
        }
    }

    void OnStatus(JsonObject message)
    {
        _remote.OnStatus(message);
        _playback.Tick();   // the screens show it
    }

    /// <summary>The patch changed on the show computer: the same fixtures and universes here, so channels are named alike.</summary>
    void OnPatch(JsonObject message)
    {
        UseProfiles(message["profiles"] as JsonArray);
        _session.Project.Fixtures = ShowJson.FromNode<List<FixtureInstance>>(message["fixtures"]!) ?? [];
        _session.Project.Universes = ShowJson.FromNode<List<UniverseConfig>>(message["universes"]!) ?? [];
        _session.Apply();   // the screens follow (track headers name channels by fixture)
    }

    /// <summary>
    /// Levels set by hand on the show computer, copied into this copy's engine: Snapshot (handles at the current
    /// levels) reads them here as it does on the desktop.
    /// </summary>
    void OnManualLevels(JsonObject message)
    {
        int universe = message["universe"]!.GetValue<int>();
        var levels = Convert.FromBase64String(message["levels"]!.GetValue<string>());
        for (int address = 1; address <= levels.Length; address++) _session.Engine.SetChannel(universe, address, levels[address - 1]);
    }

    /// <summary>The built-in fixture profiles, and the show's others (QLC+, Open Fixture Library, custom) as the show computer sent them.</summary>
    void UseProfiles(JsonArray? sent)
    {
        var profiles = BuiltInProfiles.ToDictionary(p => p.Id);
        foreach (var node in sent ?? [])
            if (node is not null) profiles[node["id"]!.GetValue<string>()] = FixtureProfileLoader.ParseJson(node.ToJsonString());
        _session.UseProfiles(new FixtureProfileLibrary(profiles.Values));
    }
}
