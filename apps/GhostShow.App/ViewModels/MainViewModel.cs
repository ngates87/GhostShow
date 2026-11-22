using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.App.Client;
using GhostShow.Core.Inputs;
using GhostShow.Core.Diagnostics;
using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Playback;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;
using GhostShow.Core.Model;
using GhostShow.Dmx;
using GhostShow.Inputs;
using GhostShow.Server;
using Microsoft.Extensions.Logging;

namespace GhostShow.App.ViewModels;

/// <summary>
/// The show computer's main view: the shared frame (<see cref="ShellViewModel"/>) on this computer's own services, plus
/// what only the show computer does: files and saving, sound, inputs, diagnostics, the web remote, and the UI refresh
/// tick that keeps every screen up to date. The tick runs at 30 Hz on the UI thread, independent of DMX output timing;
/// screens only read the engine and output services, which are thread-safe and never block on I/O.
/// </summary>
public sealed partial class MainViewModel : ShellViewModel
{
    static readonly FilePickerFileType OldShowFiles = new("GhostShow single-file show (older versions)") { Patterns = ["*.json"] };
    static readonly FilePickerFileType AudioFiles = new("Audio (WAV, MP3)") { Patterns = ["*.wav", "*.mp3", "*.WAV", "*.MP3"] };
    static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(33);

    readonly ShowServer _server;
    readonly ShowSession _session;
    readonly MachineSettings _settings;
    readonly AppPaths _paths;
    readonly IStorageProvider _storage;
    readonly ILogger _logger;
    readonly LocalOutputSetup _outputSetup;
    readonly DispatcherTimer _tick;
    readonly ShowFiles _files;
    readonly ShowControl _control;
    readonly ServerTick _serverTick;
    readonly ShowRemote _remote;
    readonly RemoteShow? _window;   // the window's own connection, when it works the show over it
    readonly CancellationTokenSource _windowStop = new();

    /// <summary>Whether the window works the show over the connection, as a device does (GHOSTSHOW_WINDOW=connected; docs/plan-remote.md, step 5).</summary>
    public static bool OverTheConnectionRequested => Environment.GetEnvironmentVariable("GHOSTSHOW_WINDOW") == "connected";

    /// <param name="overTheConnection">
    /// The screens work a copy of the show kept in step over the window's own connection to the server, as a device's
    /// do (see <see cref="RemoteShow"/>), rather than the server's show directly. What only the show computer does
    /// (Home, its file dialogs, Remote Access, the close warning, the server's refresh) still calls the server here.
    /// </param>
    public MainViewModel(ShowServices services, IStorageProvider storage, ISaveChangesPrompt savePrompt, ILogger<MainViewModel> logger,
                         bool overTheConnection = false)
        : this(new ShowServer(services, logger), storage, savePrompt, logger, overTheConnection)
    {
    }

    MainViewModel(ShowServer server, IStorageProvider storage, ISaveChangesPrompt savePrompt, ILogger logger, bool overTheConnection)
        : this(server, storage, savePrompt, logger,
               overTheConnection ? new RemoteShow(WindowConnection.To(server.Remote), FixtureProfileLoader.LoadBuiltIn()) : null)
    {
    }

    MainViewModel(ShowServer server, IStorageProvider storage, ISaveChangesPrompt savePrompt, ILogger logger, RemoteShow? window)
        : base(window?.Session ?? server.Services.Session, window?.Services ?? ShellServicesOf(server))
    {
        _server = server;
        _window = window;
        var (session, settings, paths) = (server.Services.Session, server.Services.Settings, server.Services.Paths);
        _session = session;
        _settings = settings;
        _paths = paths;
        _storage = storage;
        _logger = logger;
        _outputSetup = server.Outputs;
        server.Problem += ReportError;

        ShowPlayback = server.Playback;
        _diagnostics = server.Diagnostics;
        Diagnostics = new DiagnosticsViewModel(window?.Diagnostics ?? (IDiagnostics)_diagnostics);
        _load = server.Load;
        _triggers = server.Triggers;
        _files = server.Files;
        _control = server.Control;
        server.FocusedTimeline = () => Playback.FocusedId;   // the transport works the timeline open in the editor
        server.DeviceBlackout += () => StatusMessage = BlackoutDone;
        // A device opened a timeline: the editor shows it, and the transport works it.
        _control.OpenRequested += id =>
        {
            if (Session.Project.Timelines.FirstOrDefault(t => t.Id == id) is not { } timeline) return;
            Timeline.OpenTimeline(timeline);
            Timeline.Tree.Reveal(timeline);
        };
        _inputSetup = server.Inputs;
        Inputs = new InputsViewModel(Session, window?.Inputs ?? (IInputSetup)_inputSetup, Buttons);
        BottomWorkspace = new BottomWorkspaceViewModel(Playback, Inputs.Monitor, Lighting, Diagnostics, settings.Panels, offersPlaying: false);
        _audioSetup = server.Audio;
        _serverTick = server.Tick;
        _remote = server.Remote;
        Remote = new RemoteViewModel(_remote);
        Audio = new AudioViewModel(window?.Audio ?? (IAudioSetup)_audioSetup, AudioDevices);
        _ = AudioDevices.RefreshAsync();
        Guide = new SetupGuideViewModel(session, settings, page => CurrentPage = page, () =>
        {
            CurrentPage = Page.Timeline;
            if (Timeline.Tracks.FirstOrDefault(t => t.IsLighting) is { } lighting) Timeline.EditChannels(lighting);
        });
        server.ScreensFollowShow = ScreensFollowShow;
        if (window is not null) FollowTheConnection(window);
        _files.Changed += () =>
        {
            IsDirty = _files.IsDirty;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ShowFile));
        };
        _files.Saved += Timeline.RefreshTitles;
        _files.RecentChanged += RefreshRecentShows;
        _files.Status += message => StatusMessage = message;
        _files.Problem += ReportError;

        _unsavedChanges = new UnsavedChangesGuard(() => _files.IsDirty, () => ShowName, SaveShowAsync, savePrompt);
        _tick = new DispatcherTimer(TickInterval, DispatcherPriority.Background, (_, _) => Tick());
        FollowPanelWidths();
    }

    /// <summary>
    /// The screens on the copy of the show that comes over the connection: rebuilt on it whenever it arrives whole (on
    /// connecting, on a resync, or another show), keeping the timelines open; the triggers and inputs as the server says.
    /// </summary>
    void FollowTheConnection(RemoteShow window)
    {
        List<Guid> openTabs = [];
        Guid? showing = null;
        window.Arriving += () =>
        {
            openTabs = Timeline.Tabs.Select(t => t.Timeline.Id).ToList();
            showing = Timeline.SelectedTimeline?.Id;
            Playback.Reset();
        };
        window.Arrived += _ =>
        {
            Lighting.Reload();
            Timeline.Reload();
            Buttons.Reload();
            Inputs.Reload();
            var opened = Timeline.Tabs.FirstOrDefault();   // Reload opens the tree's first timeline
            foreach (var id in openTabs)
                if (Session.Project.Timelines.FirstOrDefault(t => t.Id == id) is { } timeline) Timeline.OpenTimeline(timeline);
            if (Session.Project.Timelines.FirstOrDefault(t => t.Id == showing) is { } shown)
            {
                Timeline.OpenTimeline(shown);
                Timeline.Tree.Reveal(shown);
            }
            if (opened is not null && !openTabs.Contains(opened.Timeline.Id) && openTabs.Count > 0 && Timeline.Tabs.Count > 1) Timeline.CloseTabCommand.Execute(opened);
            Lighting.Fixtures.Sync();
            Lighting.Outputs.Reload();
            Guide.Refresh();
        };
        window.StatusArrived += _ => Playback.Tick();   // the screens show it at once
        window.TriggersChanged += Buttons.ShowStates;
        window.InputArrived += m => RemoteTriggers.OnInput(Buttons, m);
        window.Edits.Notice += message => StatusMessage = message;
    }

    /// <summary>The server's own services, for the screens on this computer.</summary>
    static ShellServices ShellServicesOf(ShowServer server) =>
        new(server.Playback, server.Library, server.Outputs, server.Levels, LocalAudioDevices.For(server.AudioOutputs), new LocalTriggers(server.Triggers));

    // ---- the side panels' widths: this computer's, as last dragged ----

    DispatcherTimer? _savePanels;

    /// <summary>Opens the panels at their saved widths, and saves a width a moment after it's been dragged.</summary>
    void FollowPanelWidths()
    {
        var panels = _settings.Panels;
        Timeline.TreeWidth = panels.Timelines ?? TimelineEditorViewModel.UsualTreeWidth;
        Timeline.InspectorWidth = panels.Inspector ?? TimelineEditorViewModel.UsualInspectorWidth;
        BottomWorkspace.PanelHeight = panels.TimelineBottom ?? BottomWorkspaceViewModel.UsualHeight;
        BottomWorkspace.IsOpen = panels.TimelineBottomOpen ?? false;
        Buttons.PanelWidth = panels.Buttons ?? ButtonsViewModel.UsualWidth;
        // Not on every step of a drag: once it's let go (or paused).
        _savePanels = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) =>
        {
            _savePanels!.Stop();
            await SaveSettingsAsync();
        });
        _savePanels.Stop();
        Timeline.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TimelineEditorViewModel.TreeWidth)) Note(() => panels.Timelines = Timeline.TreeWidth);
            else if (e.PropertyName == nameof(TimelineEditorViewModel.InspectorWidth)) Note(() => panels.Inspector = Timeline.InspectorWidth);
        };
        BottomWorkspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BottomWorkspaceViewModel.PanelHeight)) Note(() => panels.TimelineBottom = BottomWorkspace.PanelHeight);
            else if (e.PropertyName == nameof(BottomWorkspaceViewModel.IsOpen)) Note(() => panels.TimelineBottomOpen = BottomWorkspace.IsOpen);
            else if (e.PropertyName == nameof(BottomWorkspaceViewModel.SelectedPane)) Note(() => panels.TimelineSelectedPane = BottomWorkspace.SelectedPane?.Key);
        };
        BottomWorkspace.Panes.CollectionChanged += (_, _) => Note(() => panels.TimelinePanes = BottomWorkspace.Panes.Select(p => p.Key).ToList());
        Buttons.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ButtonsViewModel.PanelWidth)) Note(() => panels.Buttons = Buttons.PanelWidth);
        };

        void Note(Action change)
        {
            change();
            _savePanels.Stop();
            _savePanels.Start();
        }
    }

    readonly UnsavedChangesGuard _unsavedChanges;

    protected override string ShowName => _files.ShowName;

    /// <summary>The open show's files (in GhostShow.Server): saving, opening, the recent list.</summary>
    public ShowFiles Files => _files;

    /// <summary>
    /// Before the window closes, every time: says what's still live (closing stops it) and offers to save unsaved
    /// changes. False means the user cancelled closing.
    /// </summary>
    public Task<bool> ConfirmCloseAsync() => _unsavedChanges.ConfirmCloseAsync(DescribeClosing());

    /// <summary>What closing now would stop: fixtures not blacked out, timelines running, devices connected.</summary>
    public CloseWarning DescribeClosing() =>
        CloseWarning.Of(_session.Engine, ShowPlayback.Running, Remote.ConnectedCount, ShowName, _files.IsDirty);

    /// <summary>Playback on this computer (the show computer): what the screens and the web remote ask to play.</summary>
    public ShowPlayback ShowPlayback { get; }

    public RemoteViewModel Remote { get; }
    public AudioViewModel Audio { get; }
    public SetupGuideViewModel Guide { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public InputsViewModel Inputs { get; }
    readonly LocalInputSetup _inputSetup;
    readonly ShowTriggers _triggers;
    readonly LocalAudioSetup _audioSetup;
    readonly LocalDiagnostics _diagnostics;
    public PerformanceMeterViewModel Performance { get; } = new();
    readonly ShowLoad _load;

    public override bool IsShowComputer => true;
    public override object? HomePage => _home ??= new HomePageViewModel(this);
    HomePageViewModel? _home;
    public override object AudioPage => Audio;
    public override object InputsPage => Inputs;
    public override object DiagnosticsPage => Diagnostics;
    public override object ButtonsPart => Buttons;
    public override object? GuidePart => Guide;
    public override object? MenuBarPart => Remote;
    public override object? StatusPart => Performance;

    public string ShowFile => _files.ShowFile;

    // ---- Home screen ----

    protected override async Task OpenRecent(RecentShowRow? row)
    {
        if (row is null) return;
        if (!row.Exists)
        {
            ReportError($"{row.Path} isn't there any more. Use × to remove it from the list.");
            return;
        }
        if (!await _unsavedChanges.ConfirmAsync("opening another show")) return;
        await OpenPathAsync(row.Path);
    }

    [RelayCommand]
    async Task ForgetRecent(RecentShowRow? row)
    {
        if (row is null) return;
        await _files.ForgetRecentAsync(row.Path);
        RefreshRecentShows();
    }

    void RefreshRecentShows()
    {
        RecentShows.Clear();
        foreach (var (path, exists) in _files.RecentShows)
            RecentShows.Add(new RecentShowRow(path, Path.GetFileName(path), Path.GetDirectoryName(path) ?? "", exists));
    }

    protected override void OnPageShown()
    {
        base.OnPageShown();
        if (CurrentPage == Page.Home) RefreshRecentShows();   // a recent show may have been moved or reconnected since
        Guide.OnTimeline = CurrentPage == Page.Timeline;
        Guide.Refresh();
        if (CurrentPage == Page.Audio)
        {
            _ = Audio.RefreshDevicesCommand.ExecuteAsync(null);
            _audioSetup.Refresh();
        }
        // Over the connection, the server keeps a screen up to date while it says it's watching.
        _window?.Audio.SetWatching(CurrentPage == Page.Audio);
        _window?.Diagnostics.SetWatching(CurrentPage == Page.Diagnostics);
        if (_window is not null && CurrentPage is >= Page.DmxSetup and <= Page.Monitor) _ = _window.Library.LoadAsync();
        _outputSetup.Refresh();
        if (CurrentPage == Page.Diagnostics) _diagnostics.Refresh();
        if (CurrentPage == Page.InputMonitor) Inputs.RefreshPortsCommand.Execute(null);   // a cable may have been plugged in
        Tick();
    }

    /// <summary>
    /// Starts output, reopens the show used last (if it's still there; otherwise Home is shown), and loads the full
    /// profile library in the background.
    /// </summary>
    public async Task StartAsync(string? settingsError)
    {
        if (settingsError is not null) ReportError(settingsError);
        RefreshRecentShows();
        await _server.StartAsync(_tick.Start);
        if (_window is not null) _ = _window.RunAsync(_windowStop.Token);
    }

    /// <summary>Stops output (each universe sends its blackout first) within the outputs' stop timeout.</summary>
    public async Task ShutdownAsync()
    {
        _tick.Stop();
        _windowStop.Cancel();
        bool clean = await _server.ShutdownAsync();   // devices are told it's going; outputs send their blackout last
        _logger.LogInformation(clean ? "Shut down cleanly." : "Shut down; an output didn't stop in time.");
    }

    const string BlackoutDone = "Blackout: playback stopped and every manual level set to 0.";

    protected override void BlackoutShow()
    {
        _server.Blackout();
        StatusMessage = BlackoutDone;
    }

    protected override async Task NewShowAsync()
    {
        if (!await _unsavedChanges.ConfirmAsync("starting a new show")) return;
        await _files.NewAsync();
        ShowEditor();
        StatusMessage = "New show. Save (Ctrl+S) picks its folder.";
    }

    /// <summary>What the last import couldn't convert (also in the log).</summary>
    public IReadOnlyList<string> ImportNotes { get; private set; } = [];

    static readonly FilePickerFileType VenueMagicProjects = new("VenueMagic project") { Patterns = ["*.vmp", "*.vmp.bak"] };

    /// <summary>
    /// File → Import VenueMagic project: a new show from a VenueMagic project's patch (its fixture types become custom
    /// types), with an empty timeline for each of its timelines. Unsaved until Save picks a folder.
    /// </summary>
    protected override async Task ImportVenueMagic()
    {
        if (!await _unsavedChanges.ConfirmAsync("importing a VenueMagic project")) return;
        var files = await _storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose a VenueMagic project (.vmp)", FileTypeFilter = [VenueMagicProjects] });
        if (files is not [var file] || file.TryGetLocalPath() is not { } path) return;
        await ImportVenueMagicAsync(path);
    }

    public async Task ImportVenueMagicAsync(string path)
    {
        Core.Import.ImportResult result;
        try
        {
            string name = Path.GetFileName(path).Split('.')[0];
            result = await Task.Run(() => Core.Import.VenueMagicImporter.Import(Core.Import.VenueMagicProject.Read(path), name));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ReportError($"Couldn't import {Path.GetFileName(path)}: {ex.Message}");
            return;
        }
        foreach (var profile in result.Profiles)
            if (await Library.SaveCustomAsync(profile) is { } error) ReportError(error);
        await _files.UseAsync(result.Project);
        ShowEditor();
        StatusMessage = $"Imported {Path.GetFileName(path)}: {result.Project.Fixtures.Count} fixtures, {result.Profiles.Count} fixture types, "
                        + $"{result.Project.Timelines.Count} timelines"
                        + (result.Notes.Count > 0 ? $"; {result.Notes.Count} notes on what wasn't converted (Diagnostics → recent events)" : "")
                        + ". Save (Ctrl+S) picks its folder.";
        foreach (var note in result.Notes) _logger.LogWarning("Import: {Note}", note);
        ImportNotes = result.Notes;
    }

    /// <summary>Closes the show (offering to save) and returns to Home.</summary>
    protected override async Task CloseShow()
    {
        if (!IsShowOpen || !await _unsavedChanges.ConfirmAsync("closing the show")) return;
        await _files.NewAsync();   // an empty show underneath, so the engine and outputs carry on (dark)
        IsShowOpen = false;
        StatusMessage = "";
    }

    /// <summary>Opens a show folder (one with a show.json).</summary>
    protected override async Task OpenShowAsync()
    {
        if (!await _unsavedChanges.ConfirmAsync("opening another show")) return;
        var folders = await _storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open show folder" });
        if (folders is not [var folder] || folder.TryGetLocalPath() is not { } path) return;
        if (!ShowFolderStore.IsShowFolder(path))
        {
            ReportError($"{Path.GetFileName(path)} isn't a show folder (it has no {ShowFolderStore.ShowFileName}). " +
                        "To open a show from an older version, use Open ▾ → Older single-file show.");
            return;
        }
        await OpenPathAsync(path);
    }

    /// <summary>Opens a show saved by an older version as a single .json file. It's saved as a folder from then on.</summary>
    protected override async Task OpenOldShow()
    {
        if (!await _unsavedChanges.ConfirmAsync("opening another show")) return;
        var files = await _storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open older single-file show", FileTypeFilter = [OldShowFiles] });
        if (files is [var file] && file.TryGetLocalPath() is { } path) await OpenPathAsync(path);
    }

    async Task OpenPathAsync(string path) => await _files.OpenAsync(path);

    /// <summary>
    /// Copies the sounds the show uses from outside its folder into its "audio" folder (see <see cref="ShowFiles.CollectAudioAsync"/>).
    /// </summary>
    protected override Task CollectAudio() => _files.CollectAudioAsync(quiet: false);

    protected override async Task SaveShowAsync()
    {
        if (_files.NeedsFirstSave) await SaveAs();
        else await _files.SaveAsync();
    }

    /// <summary>
    /// Saves the show as a folder. Pick where: an empty folder (e.g. one just made in the picker) is used as it is;
    /// anywhere else, a new folder named after the show is made inside it, so a show is never mixed into another
    /// folder's files.
    /// </summary>
    protected override async Task SaveAs()
    {
        var picked = await _storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Save show: choose a folder for it (an empty one, or a place to make it in)",
        });
        if (picked is not [var location] || location.TryGetLocalPath() is not { } path) return;
        await _files.SaveAsAsync(_files.FolderFor(path));
    }

    /// <summary>Save, asked for by a device (see <see cref="ShowFiles.SaveForDeviceAsync"/>).</summary>
    internal Task<string?> SaveForDeviceAsync() => _files.SaveForDeviceAsync();

    Task SaveSettingsAsync() => _server.SaveSettingsAsync();

    /// <summary>Another show is open: the screens follow it (the server's part is in <see cref="ShowServer"/>).</summary>
    Task ScreensFollowShow()
    {
        if (_window is not null)   // the copy arrives over the connection (FollowTheConnection)
        {
            Guide.Reset();
            ShowEditor();
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ShowFile));
            return Task.CompletedTask;
        }
        Lighting.Reload();
        Playback.Reset();    // the old show's timelines stop at once
        Timeline.Reload();   // focuses the new show's first timeline
        Buttons.Reload();
        Inputs.Reload();
        Guide.Reset();
        ShowEditor();   // (closing the show goes back to Home after this)
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ShowFile));
        return Task.CompletedTask;
    }

    protected override void MarkDirty() => _files.MarkDirty();

    protected override async Task<string?> PickAudioFileAsync()
    {
        var files = await _storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose a sound", FileTypeFilter = [AudioFiles] });
        return files is [var file] ? file.TryGetLocalPath() : null;
    }

    protected override void ReportError(string message)
    {
        base.ReportError(message);
        _logger.LogWarning("{Message}", message);
    }

    /// <summary>Screens refresh only while visible; slow-changing status refreshes once a second.</summary>
    protected override void Tick()
    {
        _serverTick.Tick();   // the server's: triggers, the load meter, outputs, inputs, warnings (run from here while the desktop hosts it)
        base.Tick();

        int count = _serverTick.Count;
        if (count % (ServerTick.PerSecond / 2) == 0) Performance.Show(_load.Reading);   // twice a second, just measured
        if (count % ServerTick.PerSecond != 0) return;
        Inputs.Tick();
        if (_window is null && CurrentPage == Page.Diagnostics && !_diagnostics.IsWatched) _diagnostics.Refresh();   // a device's keeps it up to date otherwise
        if (_window is null && CurrentPage == Page.Audio && !_audioSetup.IsWatched) _audioSetup.Refresh();
        ShowWarnings(_serverTick.Warnings);
        Guide.Refresh();
    }
}

/// <summary>The Home page's place in the main view (HomeView shows it, on the show computer only).</summary>
public sealed record HomePageViewModel(MainViewModel Main);
