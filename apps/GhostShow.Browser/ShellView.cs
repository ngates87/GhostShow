using System.Runtime.InteropServices.JavaScript;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GhostShow.App.ViewModels;
using Page = GhostShow.App.ViewModels.Page;
using GhostShow.App.Views;
using GhostShow.App.Client;
using GhostShow.Core.Diagnostics;
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
    readonly RemoteShow _show = new(BrowserPage.Endpoint(), BuiltInProfiles);
    readonly FileMenu _fileMenu;
    BrowserShellViewModel? _shell;
    bool _unsaved;
    List<Guid> _openTabs = [];
    Guid? _showing;

    ShowSession Session => _show.Session;

    public ShellView()
    {
        Background = Brushes.Black;
        Child = _message;
        _message.Text = "Connecting to the show computer…";
        _show.Edits.Notice += ShowNotice;
        _fileMenu = new FileMenu(_show.Link, ShowNotice, () => Session.Project.Name);
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _show.Arriving += () =>
        {
            // The timelines open here stay open, if they're still in the show.
            var editor = _shell?.Timeline;
            _openTabs = editor?.Tabs.Select(t => t.Timeline.Id).ToList() ?? [];
            _showing = editor?.SelectedTimeline?.Id;
            _shell?.Playback.Reset();
        };
        _show.Arrived += OnArrived;
        _show.StatusArrived += OnStatus;
        _show.DeviceChanged += ShowDevice;
        _show.TriggersChanged += states => _shell?.Buttons.ShowStates(states);
        _show.InputArrived += m =>
        {
            if (_shell is not null) RemoteTriggers.OnInput(_shell.Buttons, m);
        };
        _show.Disconnected += () =>
        {
            if (_shell is not null) ShowNotice("Lost the connection to the show computer. Trying again… Edits made now won't be kept.");
        };
        _show.Problem += m =>
        {
            if (_shell is null) _message.Text = m;
        };
    }

    /// <summary>Connects (and reconnects, whenever the connection drops) until the page is closed.</summary>
    public Task StartAsync() => _show.RunAsync();

    /// <summary>The whole show (on connecting, reconnecting, or another show was opened): the screens on the fresh copy.</summary>
    void OnArrived(JsonObject message)
    {
        if (_shell is null)
        {
            _shell = new BrowserShellViewModel(Session, _show.Services, _show.Inputs, _show.Audio, _show.Diagnostics, _show.Link, _fileMenu);
            _shell.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ShellViewModel.Title)) ShowTitle();
                // The type picker needs the whole library: only fetched once Lighting is wanted.
                else if (e.PropertyName == nameof(ShellViewModel.CurrentPage))
                {
                    if (_shell.CurrentPage is >= Page.DmxSetup and <= Page.Monitor) _ = _show.Library.LoadAsync();
                    // The show computer reads its sound files, and measures itself, only while a screen shows it.
                    _show.Audio.SetWatching(_shell.CurrentPage == Page.Audio);
                    _show.Diagnostics.SetWatching(_shell.CurrentPage == Page.Diagnostics);
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
            ReopenTabs(_openTabs, _showing);
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
            if (Session.Project.Timelines.FirstOrDefault(t => t.Id == id) is { } timeline) editor.OpenTimeline(timeline);
        if (Session.Project.Timelines.FirstOrDefault(t => t.Id == showing) is { } shown)
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
        if (Session.Project.FindTimeline(Uri.UnescapeDataString(open)) is { } timeline) _shell.Timeline.OpenTimeline(timeline);
    }

    void OnStatus(JsonObject message)
    {
        if (_shell is null) return;
        _shell.Playback.Tick();   // the screens show it at once
        var warnings = message["warnings"]?.AsArray().Select(w => w?.GetValue<string>()).Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w!).ToList() ?? [];
        bool unsaved = message["unsaved"]?.GetValue<bool>() == true;
        bool needsFirstSave = message["needsFirstSave"]?.GetValue<bool>() == true;
        _shell.OnStatus(warnings, unsaved, needsFirstSave);
        if (message["load"] is JsonObject load)
            _shell.Status.Load.Show(new LoadReading(load["level"]?.GetValue<double>() ?? 0, load["tip"]?.GetValue<string>() ?? ""));
        _fileMenu.Update(unsaved, needsFirstSave);
        if (unsaved != _unsaved)
        {
            _unsaved = unsaved;
            if (!unsaved)   // saved (from here or on the desktop): the " *" marks go
            {
                foreach (var timeline in Session.Project.Timelines) timeline.HasUnsavedChanges = false;
                _shell.Timeline.RefreshTitles();
            }
        }
    }
}
