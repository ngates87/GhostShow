using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
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
/// copy of the show kept in step with the show computer's, with a bar along the bottom (who this is, messages, whether the show is saved).
/// Edits made here go to the show computer (<see cref="RemoteEdits"/>). Each kind of message from the show computer
/// has its own handler below (see docs/remote.md for the messages).
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
    readonly RemoteEdits _edits;
    readonly TextBlock _who = new() { Foreground = Brushes.Gray, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 4) };
    readonly TextBlock _notice = new() { Foreground = Brushes.Goldenrod, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 4), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _saveState = new() { Foreground = Brushes.Gray, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 4) };
    readonly Button _blackout = new() { Content = "Blackout", Padding = new(16, 3), Margin = new(8, 3), Background = Brushes.DarkRed, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold };
    readonly TextBlock _warning = new() { Foreground = Brushes.Goldenrod, FontSize = 12, Margin = new(8, 3), TextWrapping = TextWrapping.Wrap };
    readonly StackPanel _navigation = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new(8, 4) };
    readonly ContentControl _page = new();
    readonly TextBlock _showName = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 4) };
    readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    readonly RemoteFixtureLibrary _library;
    readonly ContentControl _lightingContent = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    Control? _lightingPage;
    FixtureSetupViewModel? _fixtures;
    FixtureSetupView? _fixturesView;
    bool _showingUniverses;
    TimelineEditorViewModel? _editor;
    bool _unsaved;
    long _noticeNumber;
    string _selectedPage = "Timeline";
    JsonArray? _sentAudioDevices;

    public ShellView()
    {
        Background = Brushes.Black;
        Child = _message;
        _message.Text = "Connecting to the show computer…";
        _remote = new RemotePlayback(_link);
        _playback = new PlaybackViewModel(_remote);   // the desktop's own, over the show computer's playback
        _audioDevices = new AudioDeviceList(_link);
        _edits = new RemoteEdits(_session.Document, _link);
        _edits.Notice += ShowNotice;
        _library = new RemoteFixtureLibrary(_session, _link, BuiltInProfiles);
        _session.Document.Changed += change => Dispatcher.UIThread.Post(() => OnDocumentChanged(change));
        _blackout.Click += (_, _) => _link.Send("blackout");
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Messages arrive on the connection's thread; each is handled on the UI thread.
        _link.Hello += m => Dispatcher.UIThread.Post(() => OnHello(m));
        _link.Changed += (description, ops) => Dispatcher.UIThread.Post(() => OnChange(description, ops));
        _link.Status += m => Dispatcher.UIThread.Post(() => OnStatus(m));
        _link.Issues += m => Dispatcher.UIThread.Post(() => _remote.OnIssues(m));
        _link.Patch += m => Dispatcher.UIThread.Post(() => OnPatch(m));
        _link.Manual += m => Dispatcher.UIThread.Post(() => OnManualLevels(m));
        _link.Audio += m => Dispatcher.UIThread.Post(() => { _remote.OnAudio(m); RefreshAudio(); });
        _link.AudioDevices += m => Dispatcher.UIThread.Post(() => { _sentAudioDevices = m["devices"] as JsonArray; _audioDevices.Update(_sentAudioDevices); RefreshAudio(); });
        _link.Result += m => Dispatcher.UIThread.Post(() => _edits.OnResult(m));
        _link.History += m => Dispatcher.UIThread.Post(() => _edits.OnHistory(m));
        _link.Library += m => Dispatcher.UIThread.Post(() => _library.OnStatus(m));
        _link.Disconnected += () => Dispatcher.UIThread.Post(() =>
        {
            if (_editor is not null) ShowNotice("Lost the connection to the show computer. Trying again… Edits made now won't be kept.");
        });
        _link.Problem += m => Dispatcher.UIThread.Post(() =>
        {
            if (_editor is null) _message.Text = m;
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
        var openTabs = _editor?.Tabs.Select(t => t.Timeline.Id).ToList() ?? [];
        var showing = _editor?.SelectedTimeline?.Id;

        _who.Text = $"{message["device"]?["name"]} ({message["device"]?["role"]})";
        _sentAudioDevices = message["audioDevices"] as JsonArray;
        _saveState.IsVisible = message["device"]?["role"]?.GetValue<string>() != "operator";
        _edits.OnHello();
        _playback.Reset();
        _library.UseProfilesOfTheShow(message["profiles"] as JsonArray);
        if (message["library"] is JsonObject library) _library.OnHello(library);
        _session.Use(ShowDocument.FromSnapshot(message["show"]!));
        _showName.Text = _session.Project.Name;
        _audioDevices.Update(_sentAudioDevices);
        if (_editor is null)
        {
            _editor = new TimelineEditorViewModel(_session, _playback, () => Task.FromResult<string?>(null), () => { }, _audioDevices, _edits);
            _fixtures = new FixtureSetupViewModel(_session, new ProfileLibraryViewModel(_library), _edits);
            _lightingPage = BuildLightingPage();
            BuildShell();
            OpenFromAddress();
        }
        else
        {
            if (_notice.Text?.StartsWith("Lost the connection", StringComparison.Ordinal) == true) _notice.Text = "";
            _editor.Reload();
            ReopenTabs(openTabs, showing);
            _fixtures?.Sync();   // the same screen and selection, on the fresh copy
        }
        RefreshButtons();
        RefreshLighting();
        RefreshAudio();
        RefreshPage();
    }

    /// <summary>The browser shell: the shared Timeline screen, plus remote-safe Lighting and Audio summaries.</summary>
    void BuildShell()
    {
        var root = new DockPanel();
        var header = new DockPanel { [DockPanel.DockProperty] = Dock.Top };
        DockPanel.SetDock(_showName, Dock.Left);
        DockPanel.SetDock(_blackout, Dock.Right);
        header.Children.Add(_showName);
        header.Children.Add(_blackout);
        header.Children.Add(_navigation);
        root.Children.Add(header);

        var bottom = new DockPanel { [DockPanel.DockProperty] = Dock.Bottom };
        var buttonRow = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = _buttons, Margin = new(8, 3) };
        DockPanel.SetDock(buttonRow, Dock.Top);
        bottom.Children.Add(buttonRow);
        bottom.Children.Add(BottomBar());
        root.Children.Add(bottom);

        _page.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _page.VerticalContentAlignment = VerticalAlignment.Stretch;
        var center = new DockPanel();
        DockPanel.SetDock(_warning, Dock.Top);
        center.Children.Add(_warning);
        center.Children.Add(_page);
        root.Children.Add(center);

        foreach (var label in new[] { "Timeline", "Lighting", "Audio" })
        {
            var nav = new Button { Content = label, Padding = new(14, 5), Margin = new(2), BorderThickness = new(1) };
            nav.Click += (_, _) =>
            {
                _selectedPage = label;
                if (label == "Lighting") _ = _library.LoadAsync();   // the type picker needs the whole library: only fetched once it's wanted
                RefreshPage();
            };
            _navigation.Children.Add(nav);
        }
        Child = root;
        RefreshPage();
    }

    void RefreshPage()
    {
        if (_editor is null) return;
        _page.Content = _selectedPage switch
        {
            "Lighting" => _lightingPage,
            "Audio" => AudioPage(),
            _ => new TimelineView { DataContext = _editor, Margin = new(6) },
        };
        foreach (var child in _navigation.Children.OfType<Button>())
        {
            bool selected = Equals(child.Content?.ToString(), _selectedPage);
            child.Classes.Set("selected", selected);
            child.Background = selected ? new ImmutableSolidColorBrush(Color.Parse("#285B67")) : Brushes.Transparent;
            child.BorderBrush = selected ? new ImmutableSolidColorBrush(Color.Parse("#6FC6B8")) : Brushes.Transparent;
            child.BorderThickness = selected ? new(2) : new(1);
            child.FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    /// <summary>
    /// The Lighting page: the desktop's real Fixtures screen, on this copy of the show. The universes are still a small
    /// page built here, until the desktop's DMX setup screen comes across (see docs/plan-remote.md).
    /// </summary>
    Control BuildLightingPage()
    {
        var fixtures = new Button { Content = "Fixtures", Padding = new(12, 3) };
        var universes = new Button { Content = "Universes", Padding = new(12, 3) };
        fixtures.Click += (_, _) => { _showingUniverses = false; RefreshLighting(); };
        universes.Click += (_, _) => { _showingUniverses = true; RefreshLighting(); };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new(8, 2) };
        tabs.Children.Add(fixtures);
        tabs.Children.Add(universes);
        DockPanel.SetDock(tabs, Dock.Top);
        var page = new DockPanel { Margin = new(6, 0) };
        page.Children.Add(tabs);
        page.Children.Add(_lightingContent);
        return page;
    }

    Control UniversesPage()
    {
        var stack = new StackPanel { Spacing = 8, Margin = new(6) };
        stack.Children.Add(UniverseEditor());
        foreach (var issue in _session.Patch.Issues.Where(i => i.FixtureId is null))
            stack.Children.Add(new TextBlock { Text = issue.Message, Foreground = issue.Severity == PatchIssueSeverity.Error ? Brushes.OrangeRed : Brushes.Goldenrod, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = "Which DMX adapter drives each universe is still configured on the show computer. Undo is on the Fixtures page.", Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        return new ScrollViewer { Content = stack };
    }

    Control UniverseEditor()
    {
        var section = new StackPanel { Spacing = 4 };
        section.Children.Add(new TextBlock { Text = $"Show universes · {_session.Project.Universes.Count}", FontWeight = FontWeight.SemiBold });
        foreach (var universe in _session.Project.Universes)
        {
            var name = new TextBox { Text = universe.Name, PlaceholderText = "Universe name", MinWidth = 180 };
            name.LostFocus += (_, _) => EditModel("Rename universe", universe, "name", () => universe.Name = name.Text ?? "");
            var number = new NumericUpDown { Minimum = Project.FirstUniverse, Maximum = Project.LastUniverse, Increment = 1, Value = universe.Number, Width = 110 };
            number.LostFocus += (_, _) => EditModel("Renumber universe", universe, "number", () => universe.Number = (int)(number.Value ?? universe.Number));
            var remove = new Button { Content = "Remove", Padding = new(8, 3) };
            remove.Click += (_, _) => _session.Document.Record("Remove universe", () => _session.Project.Universes.Remove(universe), ShowDocument.Shallow(_session.Project));
            var usage = new UniverseUsage(_session.Patch.Fixtures.Where(f => f.Universe == universe.Number));
            var used = new TextBlock
            {
                Text = $"{usage.UsedChannels}/512 channels used" + (usage.OverlapRanges.Count == 0 ? "" : $" · overlap at {string.Join(", ", usage.OverlapRanges)}"),
                Foreground = usage.OverlapRanges.Count == 0 ? Brushes.Gray : Brushes.OrangeRed,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            section.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { name, number, remove, used } });
        }
        var nextNumber = NextUniverseNumber();
        var add = new Button { Content = "+ Universe", HorizontalAlignment = HorizontalAlignment.Left, Padding = new(10, 3), IsEnabled = nextNumber is not null };
        add.Click += (_, _) =>
        {
            if (NextUniverseNumber() is not { } next) return;
            var universe = new UniverseConfig { Number = next, Name = $"Universe {next}" };
            _session.Document.Record("Add universe", () => _session.Project.Universes.Add(universe), ShowDocument.Shallow(_session.Project));
        };
        section.Children.Add(add);
        return new Border { Classes = { "card" }, Padding = new(10), Child = section };
    }

    int? NextUniverseNumber()
    {
        var used = _session.Project.Universes.Select(u => u.Number).ToHashSet();
        for (int number = Project.FirstUniverse; number <= Project.LastUniverse; number++)
            if (!used.Contains(number)) return number;
        return null;
    }

    void EditModel(string description, object model, string field, Action apply)
    {
        _session.Document.Record(description, apply, [model], mergeKey: (model.GetHashCode(), field));
    }

    void OnDocumentChanged(ShowChange change)
    {
        RefreshButtons();
        RefreshLighting();
        RefreshAudio();
    }

    Control AudioPage()
    {
        var stack = new StackPanel { Spacing = 8, Margin = new(16) };
        stack.Children.Add(new TextBlock { Text = "Audio", Classes = { "heading" } });
        stack.Children.Add(new TextBlock { Text = AudioDevicesText(), Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8) });
        var rows = AudioRows();
        if (rows.Count == 0) stack.Children.Add(new TextBlock { Text = "No audio events in this show yet." });
        else foreach (var row in rows)
        {
            stack.Children.Add(new Border { Classes = { "card" }, Padding = new(10, 6), Margin = new(0, 2), Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = row.File, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = $"{row.Used} · {row.Length}", Opacity = 0.75 },
                }
            } });
        }
        stack.Children.Add(new TextBlock { Text = "Output device and volume controls are on the show computer.", Opacity = 0.65, Margin = new(0, 8), TextWrapping = TextWrapping.Wrap });
        return new ScrollViewer { Content = stack };
    }

    List<(string File, string Used, string Length)> AudioRows()
    {
        var result = new List<(string, string, string)>();
        foreach (var timeline in _session.Project.Timelines)
            foreach (var audio in timeline.Tracks.SelectMany(t => t.Events).OfType<GhostShow.Core.Timeline.AudioEvent>())
            {
                var length = _remote.AudioLength(audio.File);
                result.Add((audio.File, timeline.TreePath, length is { } time ? time.ToString(@"m\:ss") : "Measuring…"));
            }
        return result;
    }

    string AudioDevicesText() => _sentAudioDevices is { Count: > 0 }
        ? $"Audio devices on the show computer: {string.Join(", ", _sentAudioDevices.Select(d => d?.GetValue<string>()).Where(n => n is not null))}"
        : "Audio devices are managed on the show computer.";

    void RefreshAudio()
    {
        if (_selectedPage == "Audio") RefreshPage();
    }

    void RefreshLighting()
    {
        // Only the universes page is rebuilt: the Fixtures screen follows the show by itself.
        if (_showingUniverses) _lightingContent.Content = UniversesPage();
        else if (_fixtures is not null) _lightingContent.Content = _fixturesView ??= new FixtureSetupView { DataContext = _fixtures };
    }

    void RefreshButtons()
    {
        _buttons.Children.Clear();
        foreach (var showButton in _session.Project.Buttons)
        {
            var button = new Button { Content = showButton.Name, Padding = new(14, 8), MinWidth = 90 };
            if (showButton.Color is { } color && Color.TryParse(color, out var parsed)) button.Background = new ImmutableSolidColorBrush(parsed);
            var id = showButton.Id;
            button.Click += (_, _) => _link.Send("press", ("button", id.ToString()));
            _buttons.Children.Add(button);
        }
        _buttons.IsVisible = _session.Project.Buttons.Count > 0;
    }

    /// <summary>Who this is, messages, and whether the show is saved. There's no Save button: changes save themselves on the show computer.</summary>
    Control BottomBar()
    {
        var bar = new DockPanel { [DockPanel.DockProperty] = Dock.Bottom };
        DockPanel.SetDock(_who, Dock.Left);
        DockPanel.SetDock(_saveState, Dock.Right);
        bar.Children.Add(_who);
        bar.Children.Add(_saveState);
        bar.Children.Add(_notice);
        return bar;
    }

    /// <summary>After a fresh copy of the show: the same tabs as before, the same one showing.</summary>
    void ReopenTabs(List<Guid> openTabs, Guid? showing)
    {
        var editor = _editor!;
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
        _edits.Save();
        e.Handled = true;
    }

    /// <summary>A message along the bottom, for a few seconds.</summary>
    void ShowNotice(string text)
    {
        _notice.Text = text;
        long number = ++_noticeNumber;
        DispatcherTimer.RunOnce(() =>
        {
            if (number == _noticeNumber) _notice.Text = "";
        }, TimeSpan.FromSeconds(6));
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
        _playback.Tick();   // the screens show it
        var warnings = message["warnings"]?.AsArray().Select(w => w?.GetValue<string>()).Where(w => !string.IsNullOrWhiteSpace(w)).ToList() ?? [];
        _warning.Text = warnings.Count == 0 ? "" : $"⚠ {string.Join("  ·  ", warnings)}";
        _warning.IsVisible = warnings.Count > 0;

        bool unsaved = message["unsaved"]?.GetValue<bool>() == true;
        bool needsFirstSave = message["needsFirstSave"]?.GetValue<bool>() == true;
        _saveState.Text = !unsaved ? "Saved"
            : needsFirstSave ? "Not saved: choose its folder on the show computer (File → Save)"
            : "Saving…";
        if (unsaved != _unsaved)
        {
            _unsaved = unsaved;
            if (!unsaved)   // saved (from here or on the desktop): the " *" marks go
            {
                foreach (var timeline in _session.Project.Timelines) timeline.HasUnsavedChanges = false;
                _editor?.RefreshTitles();
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
        _showName.Text = _session.Project.Name;
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
