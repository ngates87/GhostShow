using System.Runtime.InteropServices.JavaScript;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GhostShow.App.ViewModels;
using Page = GhostShow.App.ViewModels.Page;
using GhostShow.App.Views;
using GhostShow.Browser.Remote;
using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhostShow.Browser;

/// <summary>
/// The page: says what it's doing until the show has arrived, then shows the desktop app's own main view
/// (<see cref="MainView"/>, on a <see cref="BrowserShellViewModel"/>) on a copy of the show kept in step with the show
/// computer's. Edits made here go to the show computer (<see cref="RemoteEdits"/>). Each kind of message from the show
/// computer has its own handler below (see docs/remote.md for the messages).
/// </summary>
sealed class ShellView : Border
{
    static readonly IReadOnlyList<FixtureProfile> BuiltInProfiles = FixtureProfileLoader.LoadBuiltIn();

    readonly TextBlock _message = new() { Foreground = Brushes.Gray, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new(24) };
    readonly ShowSession _session = new(new ShowEngine(), new FixtureProfileLibrary(BuiltInProfiles), NullLogger<ShowSession>.Instance);
    readonly RemoteLink _link = new();
    readonly RemotePlayback _remote;
    readonly AudioDeviceList _audioDevices;
    readonly RemoteEdits _edits;
    readonly FileMenu _fileMenu;
    readonly RemoteFixtureLibrary _library;
    readonly RemoteOutputSetup _outputSetup;
    readonly RemoteLiveLevels _levels;
    readonly RemoteTriggers _triggers;
    readonly RemoteInputSetup _inputSetup;
    readonly RemoteAudioSetup _audioSetup;
    BrowserShellViewModel? _shell;
    bool _unsaved;

    public ShellView()
    {
        Background = Brushes.Black;
        Child = _message;
        _message.Text = "Connecting to the show computer…";
        _remote = new RemotePlayback(_link);
        _audioDevices = new AudioDeviceList(_link);
        _edits = new RemoteEdits(_session.Document, _link);
        _edits.Notice += ShowNotice;
        _fileMenu = new FileMenu(_link, ShowNotice, () => _session.Project.Name);
        _library = new RemoteFixtureLibrary(_session, _link, BuiltInProfiles);
        _outputSetup = new RemoteOutputSetup(_link);
        _levels = new RemoteLiveLevels(_session, _link);
        _triggers = new RemoteTriggers(_link);
        _inputSetup = new RemoteInputSetup(_link);
        _audioSetup = new RemoteAudioSetup(_link);
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Messages arrive on the connection's thread; each is handled on the UI thread.
        _link.Hello += m => Dispatcher.UIThread.Post(() => OnHello(m));
        _link.Changed += (description, ops) => Dispatcher.UIThread.Post(() => OnChange(description, ops));
        _link.Status += m => Dispatcher.UIThread.Post(() => OnStatus(m));
        _link.Issues += m => Dispatcher.UIThread.Post(() => _remote.OnIssues(m));
        _link.Patch += m => Dispatcher.UIThread.Post(() => OnPatch(m));
        _link.Manual += m => Dispatcher.UIThread.Post(() => OnManualLevels(m));
        _link.Audio += m => Dispatcher.UIThread.Post(() => _remote.OnAudio(m));
        _link.AudioDevices += m => Dispatcher.UIThread.Post(() => _audioDevices.Update(m["devices"] as JsonArray));
        _link.Result += m => Dispatcher.UIThread.Post(() => _edits.OnResult(m));
        _link.History += m => Dispatcher.UIThread.Post(() => _edits.OnHistory(m));
        _link.Library += m => Dispatcher.UIThread.Post(() => _library.OnStatus(m));
        _link.Outputs += m => Dispatcher.UIThread.Post(() => _outputSetup.OnOutputs(m));
        _link.Levels += m => Dispatcher.UIThread.Post(() => _levels.OnLevels(m));
        _link.Device += m => Dispatcher.UIThread.Post(() => ShowDevice(m["device"]));
        _link.Triggers += m => Dispatcher.UIThread.Post(() => _shell?.Buttons.ShowStates(RemoteTriggers.Read(m)));
        _link.AudioSetup += m => Dispatcher.UIThread.Post(() => _audioSetup.OnAudio(m));
        _link.Inputs += m => Dispatcher.UIThread.Post(() => _inputSetup.OnInputs(m));
        _link.Input += m => Dispatcher.UIThread.Post(() =>
        {
            if (_shell is not null) RemoteTriggers.OnInput(_shell.Buttons, m);
        });
        _link.Disconnected += () => Dispatcher.UIThread.Post(() =>
        {
            if (_shell is not null) ShowNotice("Lost the connection to the show computer. Trying again… Edits made now won't be kept.");
        });
        _link.Problem += m => Dispatcher.UIThread.Post(() =>
        {
            if (_shell is null) _message.Text = m;
        });
    }

    /// <summary>Connects (and reconnects, whenever the connection drops) until the page is closed.</summary>
    public Task StartAsync() => _link.RunAsync();

    /// <summary>
    /// The whole show (on connecting, reconnecting, or another show was opened): a fresh copy, and the screens on it.
    /// The timelines open here stay open, if they're still in the show.
    /// </summary>
    void OnHello(JsonObject message)
    {
        var editor = _shell?.Timeline;
        var openTabs = editor?.Tabs.Select(t => t.Timeline.Id).ToList() ?? [];
        var showing = editor?.SelectedTimeline?.Id;

        _edits.OnHello();
        _shell?.Playback.Reset();
        _library.UseProfilesOfTheShow(message["profiles"] as JsonArray);
        if (message["library"] is JsonObject library) _library.OnHello(library);
        _session.Use(ShowDocument.FromSnapshot(message["show"]!));
        _outputSetup.OnHello();
        _levels.OnHello();
        _inputSetup.OnHello();
        _audioSetup.OnHello();
        _audioDevices.Update(message["audioDevices"] as JsonArray);
        if (_shell is null)
        {
            _shell = new BrowserShellViewModel(_session, new ShellServices(_remote, _library, _outputSetup, _levels, _audioDevices, _edits, _triggers), _inputSetup, _audioSetup, _link, _fileMenu);
            _shell.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ShellViewModel.Title)) ShowTitle();
                // The type picker needs the whole library: only fetched once Lighting is wanted.
                else if (e.PropertyName == nameof(ShellViewModel.CurrentPage))
                {
                    if (_shell.CurrentPage is >= Page.DmxSetup and <= Page.Monitor) _ = _library.LoadAsync();
                    _audioSetup.SetWatching(_shell.CurrentPage == Page.Audio);   // the show computer reads its sound files only while it's shown
                }
            };
            Child = new Panel { Children = { new MainView { DataContext = _shell }, _fileMenu.Questions } };
            OpenFromAddress();
        }
        else
        {
            if (_shell.StatusMessage?.StartsWith("Lost the connection", StringComparison.Ordinal) == true) _shell.StatusMessage = "";
            _shell.Timeline.Reload();
            _shell.Buttons.Reload();
            _shell.Inputs.Reload();
            ReopenTabs(openTabs, showing);
            _shell.Lighting.Fixtures.Sync();   // the same screen and selection, on the fresh copy
            _shell.Lighting.Outputs.Reload();
        }
        ShowDevice(message["device"]);
        ShowTitle();
    }

    /// <summary>Who this device is paired as (in the hello, and again when its role is changed on the show computer).</summary>
    void ShowDevice(JsonNode? device)
    {
        bool isOperator = device?["role"]?.GetValue<string>() == "operator";
        _shell?.OnHello($"{device?["name"]} ({device?["role"]})", isOperator);
    }

    /// <summary>The page's title: the show's, as the desktop's window has.</summary>
    void ShowTitle()
    {
        if (_shell is not null) JSHost.GlobalThis.GetPropertyAsJSObject("document")?.SetProperty("title", _shell.Title);
    }

    /// <summary>After a fresh copy of the show: the same tabs as before, the same one showing.</summary>
    void ReopenTabs(List<Guid> openTabs, Guid? showing)
    {
        var editor = _shell!.Timeline;
        var opened = editor.Tabs.FirstOrDefault();   // Reload opens the tree's first timeline
        foreach (var id in openTabs)
            if (_session.Project.Timelines.FirstOrDefault(t => t.Id == id) is { } timeline) editor.OpenTimeline(timeline);
        if (_session.Project.Timelines.FirstOrDefault(t => t.Id == showing) is { } shown)
        {
            editor.OpenTimeline(shown);
            editor.Tree.Reveal(shown);
        }
        if (opened is not null && !openTabs.Contains(opened.Timeline.Id) && editor.Tabs.Count > 1) editor.CloseTabCommand.Execute(opened);
    }

    /// <summary>Ctrl+S: saves now rather than a moment later, and keeps the browser's own "save this page" away.</summary>
    void OnKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.S || !e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) return;
        if (_shell is { IsEditor: true }) _fileMenu.Save();
        e.Handled = true;
    }

    void ShowNotice(string text) => _shell?.ShowNotice(text);

    /// <summary>
    /// <c>/app/?open=Rooms/Lobby</c> opens that timeline (by its place in the tree) once the show has arrived: a
    /// bookmark for the timeline you're working on.
    /// </summary>
    void OpenFromAddress()
    {
        var query = JSHost.GlobalThis.GetPropertyAsJSObject("location")?.GetPropertyAsString("search") ?? "";
        var open = query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == "open" && p.Length == 2)?[1];
        if (open is null || _shell is null) return;
        if (_session.Project.FindTimeline(Uri.UnescapeDataString(open)) is { } timeline) _shell.Timeline.OpenTimeline(timeline);
    }

    /// <summary>A change made on the show computer (or another device): applied to this copy, the same way.</summary>
    void OnChange(string description, IReadOnlyList<EditOp> ops)
    {
        _edits.OnRemoteChange();
        try
        {
            _session.Document.Apply(new Transaction(description, ops), origin: "show computer");   // an origin: it isn't sent back
        }
        catch (EditException)
        {
            _link.Resync();   // out of step: get the whole show again
        }
    }

    void OnStatus(JsonObject message)
    {
        _remote.OnStatus(message);
        if (_shell is null) return;
        _shell.Playback.Tick();   // the screens show it at once
        var warnings = message["warnings"]?.AsArray().Select(w => w?.GetValue<string>()).Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w!).ToList() ?? [];
        bool unsaved = message["unsaved"]?.GetValue<bool>() == true;
        bool needsFirstSave = message["needsFirstSave"]?.GetValue<bool>() == true;
        _shell.OnStatus(warnings, unsaved, needsFirstSave);
        _fileMenu.Update(unsaved, needsFirstSave);
        if (unsaved != _unsaved)
        {
            _unsaved = unsaved;
            if (!unsaved)   // saved (from here or on the desktop): the " *" marks go
            {
                foreach (var timeline in _session.Project.Timelines) timeline.HasUnsavedChanges = false;
                _shell.Timeline.RefreshTitles();
            }
        }
    }

    /// <summary>
    /// The patch changed on the show computer. The fixtures and universes themselves arrive as edits, like every other
    /// change; this brings the profiles they use, so channels are named alike.
    /// </summary>
    void OnPatch(JsonObject message)
    {
        _library.UseProfilesOfTheShow(message["profiles"] as JsonArray);
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
}
