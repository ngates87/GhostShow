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
    readonly List<FixtureProfile> _profileResults = [];
    TimelineEditorViewModel? _editor;
    bool _unsaved;
    long _noticeNumber;
    string _selectedPage = "Timeline";
    JsonArray? _sentAudioDevices;
    string? _profileQuery;

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
        _link.Profiles += m => Dispatcher.UIThread.Post(() => OnProfiles(m));
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
        UseProfiles(message["profiles"] as JsonArray);
        _session.Use(ShowDocument.FromSnapshot(message["show"]!));
        _showName.Text = _session.Project.Name;
        _audioDevices.Update(_sentAudioDevices);
        if (_editor is null)
        {
            _editor = new TimelineEditorViewModel(_session, _playback, () => Task.FromResult<string?>(null), () => { }, _audioDevices, _edits);
            BuildShell();
            OpenFromAddress();
        }
        else
        {
            if (_notice.Text?.StartsWith("Lost the connection", StringComparison.Ordinal) == true) _notice.Text = "";
            _editor.Reload();
            ReopenTabs(openTabs, showing);
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
            nav.Click += (_, _) => { _selectedPage = label; RefreshPage(); };
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
            "Lighting" => LightingPage(),
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

    Control LightingPage()
    {
        var page = new DockPanel { Margin = new(10) };
        var heading = new StackPanel { Spacing = 3, Margin = new(6, 2, 6, 10) };
        heading.Children.Add(new TextBlock { Text = "Lighting · Fixture patch", FontSize = 18, FontWeight = FontWeight.SemiBold });
        heading.Children.Add(new TextBlock { Text = "Changes are sent to the show computer immediately and saved automatically.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        DockPanel.SetDock(heading, Dock.Top);
        page.Children.Add(heading);

        var stack = new StackPanel { Spacing = 8, Margin = new(6, 0) };
        stack.Children.Add(UniverseEditor());
        stack.Children.Add(FixtureEditor());
        stack.Children.Add(ProfileSearch());
        foreach (var issue in _session.Patch.Issues.Where(i => i.FixtureId is null))
            stack.Children.Add(new TextBlock { Text = issue.Message, Foreground = issue.Severity == PatchIssueSeverity.Error ? Brushes.OrangeRed : Brushes.Goldenrod, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = "Which DMX adapter drives each universe is still configured on the show computer.", Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        page.Children.Add(new ScrollViewer
        {
            Content = stack,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            MinHeight = 300,
        });
        return page;
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

    Control FixtureEditor()
    {
        var section = new StackPanel { Spacing = 4 };
        section.Children.Add(new TextBlock { Text = $"Fixtures · {_session.Project.Fixtures.Count}", FontWeight = FontWeight.SemiBold });
        if (_session.Project.Fixtures.Count == 0) section.Children.Add(new TextBlock { Text = "No fixtures are patched yet.", Opacity = 0.7 });
        foreach (var fixture in _session.Project.Fixtures) section.Children.Add(FixtureRow(fixture));
        return new Border { Classes = { "card" }, Padding = new(10), Child = section };
    }

    Control FixtureRow(FixtureInstance fixture)
    {
        var profile = _session.Profiles.Find(fixture.ProfileId);
        var mode = profile?.FindMode(fixture.ModeName);
        var name = new TextBox { Text = fixture.Name, PlaceholderText = "Fixture name", MinWidth = 150 };
        name.LostFocus += (_, _) => EditModel("Rename fixture", fixture, "name", () => fixture.Name = name.Text ?? "");
        var group = new TextBox { Text = fixture.Group, PlaceholderText = "Group (optional)", MinWidth = 140 };
        group.LostFocus += (_, _) => EditModel("Change fixture group", fixture, "group", () => fixture.Group = string.IsNullOrWhiteSpace(group.Text) ? null : group.Text);
        var universe = new NumericUpDown { Minimum = Project.FirstUniverse, Maximum = Project.LastUniverse, Increment = 1, Value = fixture.Universe, Width = 120 };
        universe.LostFocus += (_, _) => EditModel("Move fixture to universe", fixture, "universe", () => fixture.Universe = (int)(universe.Value ?? fixture.Universe));
        var address = new NumericUpDown { Minimum = 1, Maximum = 512, Increment = 1, Value = fixture.StartAddress, Width = 120 };
        address.LostFocus += (_, _) => EditModel("Readdress fixture", fixture, "address", () => fixture.StartAddress = (int)(address.Value ?? fixture.StartAddress));
        var enabled = new CheckBox { Content = "Enabled", IsChecked = fixture.Enabled, VerticalAlignment = VerticalAlignment.Center };
        enabled.IsCheckedChanged += (_, _) => EditModel("Enable fixture", fixture, "enabled", () => fixture.Enabled = enabled.IsChecked == true);
        var remove = new Button { Content = "Remove", Padding = new(8, 3) };
        remove.Click += (_, _) => _session.Document.Record("Remove fixture", () => _session.Project.Fixtures.Remove(fixture), ShowDocument.Shallow(_session.Project));
        var duplicate = new Button { Content = "Duplicate", Padding = new(8, 3) };
        duplicate.Click += (_, _) => DuplicateFixture(fixture, mode);

        var knownProfiles = _session.Profiles.Profiles.Concat(_profileResults).DistinctBy(p => p.Id).ToList();
        var profilePicker = new ComboBox { ItemsSource = knownProfiles, SelectedItem = knownProfiles.FirstOrDefault(p => p.Id == fixture.ProfileId), MinWidth = 220 };
        profilePicker.ItemTemplate = ProfileTemplate();
        var modePicker = new ComboBox { ItemsSource = profile?.Modes, SelectedItem = mode, MinWidth = 180 };
        modePicker.ItemTemplate = ModeTemplate();
        bool updatingMode = false;
        profilePicker.SelectionChanged += (_, _) =>
        {
            if (profilePicker.SelectedItem is not FixtureProfile selected) return;
            EnsureProfile(selected);
            var selectedMode = selected.FindMode(fixture.ModeName) ?? selected.Modes.FirstOrDefault();
            updatingMode = true;
            modePicker.ItemsSource = selected.Modes;
            modePicker.SelectedItem = selectedMode;
            updatingMode = false;
            if (selectedMode is not null && (fixture.ProfileId != selected.Id || fixture.ModeName != selectedMode.Name))
                EditModel("Change fixture type", fixture, "profile-mode", () => { fixture.ProfileId = selected.Id; fixture.ModeName = selectedMode.Name; });
        };
        modePicker.SelectionChanged += (_, _) =>
        {
            if (updatingMode || profilePicker.SelectedItem is not FixtureProfile selected || modePicker.SelectedItem is not FixtureMode selectedMode) return;
            EnsureProfile(selected);
            if (fixture.ProfileId != selected.Id || fixture.ModeName != selectedMode.Name)
                EditModel("Change fixture mode", fixture, "profile-mode", () => { fixture.ProfileId = selected.Id; fixture.ModeName = selectedMode.Name; });
        };
        var details = new TextBlock { Text = mode is null ? $"Missing profile {fixture.ProfileId} / {fixture.ModeName}" : $"{profile} · {mode.Name} ({mode.Channels.Count} ch)", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        var fixtureIssues = _session.Patch.Issues.Where(i => i.FixtureId == fixture.Id).ToList();
        var row = new StackPanel { Spacing = 3, Children =
        {
            new WrapPanel { Orientation = Orientation.Horizontal, Children = { name, group, universe, address, enabled, duplicate, remove } },
            new WrapPanel { Orientation = Orientation.Horizontal, Children = { profilePicker, modePicker } }, details
        }};
        foreach (var issue in fixtureIssues)
            row.Children.Add(new TextBlock { Text = issue.Message, Foreground = issue.Severity == PatchIssueSeverity.Error ? Brushes.OrangeRed : Brushes.Goldenrod, TextWrapping = TextWrapping.Wrap });
        return new Border { Classes = { "card" }, Padding = new(8, 5), Child = row };
    }

    // A ComboBox with nothing selected still builds its template, with null. These must cope: an exception here happens
    // during layout, and layout never recovers from one, so every page stays blank until the browser reloads.
    static Avalonia.Controls.Templates.FuncDataTemplate<FixtureProfile?> ProfileTemplate() =>
        new((profile, _) => new TextBlock { Text = profile?.ToString() }, true);

    static Avalonia.Controls.Templates.FuncDataTemplate<FixtureMode?> ModeTemplate() =>
        new((mode, _) => new TextBlock { Text = mode is null ? "" : $"{mode.Name} · {mode.Channels.Count} channels" }, true);

    void EnsureProfile(FixtureProfile profile)
    {
        if (ReferenceEquals(_session.Profiles.Find(profile.Id), profile)) return;
        var profiles = _session.Profiles.Profiles.Where(p => !string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase))
            .Concat(_profileResults.Where(p => !string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase)))
            .Append(profile);
        _session.UseProfiles(new FixtureProfileLibrary(profiles.DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase)));
    }

    void DuplicateFixture(FixtureInstance source, FixtureMode? mode)
    {
        var count = mode?.Channels.Count ?? 1;
        int start = new UniverseUsage(_session.Patch.Fixtures.Where(f => f.Universe == source.Universe)).FirstFreeStart(count) ?? source.StartAddress;
        var copy = new FixtureInstance
        {
            Name = $"{source.Name} copy", Group = source.Group, ProfileId = source.ProfileId, ModeName = source.ModeName,
            Universe = source.Universe, StartAddress = start, Enabled = source.Enabled,
        };
        _session.Document.Record("Duplicate fixture", () => _session.Project.Fixtures.Add(copy), ShowDocument.Shallow(_session.Project));
    }

    Control ProfileSearch()
    {
        var section = new StackPanel { Spacing = 5 };
        section.Children.Add(new TextBlock { Text = "Find a fixture type", FontWeight = FontWeight.SemiBold });
        var search = new TextBox { Text = _profileQuery, PlaceholderText = "Search the show computer's fixture library", MinWidth = 260 };
        var find = new Button { Content = "Search", Padding = new(10, 3) };
        void Search()
        {
            string query = (search.Text ?? "").Trim();
            if (query.Length == 0)
            {
                ShowNotice("Enter a fixture manufacturer or model to search.");
                return;
            }
            _profileResults.Clear();
            _profileQuery = _link.Send("profiles", ("query", query)) is null ? null : query;
            if (_profileQuery is null) ShowNotice("Not connected: can't search the fixture library.");
        }
        find.Click += (_, _) => Search();
        search.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Search(); e.Handled = true; } };
        var result = new ComboBox { ItemsSource = _profileResults, MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
        result.ItemTemplate = ProfileTemplate();
        var mode = new ComboBox { MinWidth = 200 };
        mode.ItemTemplate = ModeTemplate();
        result.SelectionChanged += (_, _) =>
        {
            mode.ItemsSource = (result.SelectedItem as FixtureProfile)?.Modes;
            mode.SelectedIndex = 0;
        };
        var add = new Button { Content = "Add fixture", Padding = new(10, 3) };
        add.Click += (_, _) =>
        {
            if (result.SelectedItem is not FixtureProfile selected || mode.SelectedItem is not FixtureMode selectedMode) return;
            if (selectedMode.Channels.Count is 0 or > 512)
            {
                ShowNotice($"{selected} · {selectedMode.Name} can't be patched because it has {selectedMode.Channels.Count} channels.");
                return;
            }
            EnsureProfile(selected);
            var allocation = FindFixtureAddress(selectedMode.Channels.Count);
            if (allocation is null)
            {
                ShowNotice("No free DMX address is available in any universe.");
                return;
            }
            var (universeNumber, start, createUniverse) = allocation.Value;
            var fixture = new FixtureInstance { Name = selected.Model, ProfileId = selected.Id, ModeName = selectedMode.Name, Universe = universeNumber, StartAddress = start };
            _session.Document.Record(createUniverse ? "Add universe and fixture" : "Add fixture", () =>
            {
                if (createUniverse) _session.Project.Universes.Add(new UniverseConfig { Number = universeNumber, Name = $"Universe {universeNumber}" });
                _session.Project.Fixtures.Add(fixture);
            }, ShowDocument.Shallow(_session.Project));
        };
        var searchRow = new WrapPanel { Orientation = Orientation.Horizontal, Children = { search, find } };
        var addRow = new WrapPanel { Orientation = Orientation.Horizontal, Children = { result, mode, add } };
        section.Children.Add(new TextBlock { Text = "Search the show computer's fixture library, choose a mode, then add it at the first free address.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        section.Children.Add(searchRow);
        if (_profileQuery is not null)
            section.Children.Add(new TextBlock
            {
                Text = _profileResults.Count == 0 ? $"No fixture types found for ‘{_profileQuery}’." : $"{_profileResults.Count} fixture type(s) found.",
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
            });
        section.Children.Add(addRow);
        return new Border { Classes = { "card" }, Padding = new(10), Child = section };
    }

    (int Universe, int Address, bool CreateUniverse)? FindFixtureAddress(int channelCount)
    {
        foreach (var universe in _session.Project.Universes.OrderBy(u => u.Number))
        {
            var address = new UniverseUsage(_session.Patch.Fixtures.Where(f => f.Universe == universe.Number)).FirstFreeStart(channelCount);
            if (address is { } start) return (universe.Number, start, false);
        }

        if (NextUniverseNumber() is not { } next) return null;
        return (next, 1, true);
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

    void OnProfiles(JsonObject message)
    {
        if (message["query"]?.GetValue<string>() != _profileQuery) return;
        _profileResults.Clear();
        foreach (var node in message["profiles"]?.AsArray() ?? [])
            if (node is not null) _profileResults.Add(FixtureProfileLoader.ParseJson(node.ToJsonString()));
        if (_profileResults.Count == 0) ShowNotice($"No fixture types found for ‘{_profileQuery}’.");
        RefreshLighting();
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
        if (_selectedPage == "Lighting") RefreshPage();
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

    /// <summary>The patch changed on the show computer: the same fixtures and universes here, so channels are named alike.</summary>
    void OnPatch(JsonObject message)
    {
        UseProfiles(message["profiles"] as JsonArray);
        _session.Project.Fixtures = ShowJson.FromNode<List<FixtureInstance>>(message["fixtures"]!) ?? [];
        _session.Project.Universes = ShowJson.FromNode<List<UniverseConfig>>(message["universes"]!) ?? [];
        _session.Apply();   // the screens follow (track headers name channels by fixture)
        _showName.Text = _session.Project.Name;
        RefreshLighting();
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
