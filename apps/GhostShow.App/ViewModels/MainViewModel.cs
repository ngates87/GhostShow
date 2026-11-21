using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    readonly ShowServices _services;
    readonly ShowSession _session;
    readonly OutputManager _outputs;
    readonly MachineSettings _settings;
    readonly AppPaths _paths;
    readonly IStorageProvider _storage;
    readonly ILogger _logger;
    readonly LocalOutputSetup _outputSetup;
    readonly SettingsSaver _settingsSaver;
    readonly DispatcherTimer _tick;
    readonly ShowFiles _files;
    readonly ShowControl _control;
    int _tickCount;

    public MainViewModel(ShowServices services, IStorageProvider storage, ISaveChangesPrompt savePrompt, ILogger<MainViewModel> logger)
        : this(services, storage, savePrompt, logger, LocalServices.For(services, logger))
    {
    }

    MainViewModel(ShowServices services, IStorageProvider storage, ISaveChangesPrompt savePrompt, ILogger logger, LocalServices local)
        : base(services.Session, local.Shell)
    {
        _services = services;
        var (session, outputs, settings, paths) = (services.Session, services.Outputs, services.Settings, services.Paths);
        _session = session;
        _outputs = outputs;
        _settings = settings;
        _paths = paths;
        _storage = storage;
        _logger = logger;
        _outputSetup = local.OutputSetup;
        _settingsSaver = local.Saver;
        _settingsSaver.Problem = ReportError;

        ShowPlayback = local.Playback;
        // A sound's length was measured (on a worker thread): timelines using it are compiled again, on this thread.
        services.Audio.LengthMeasured += path => Dispatcher.UIThread.Post(() => ShowPlayback.OnAudioMeasured(path));
        _diagnostics = new LocalDiagnostics(services, () => Playback.Timeline);
        Diagnostics = new DiagnosticsViewModel(_diagnostics);
        Performance = new PerformanceMeterViewModel(() => services.PlaybackLoop.Timing, () => services.Probe.Snapshot(last: 20).P95Ms);
        _triggers = local.Triggers;
        _files = new ShowFiles(session, settings, paths, SaveSettingsAsync, services.Thread);
        _control = new ShowControl(session, local.Playback, _triggers, _files, () => Playback.FocusedId, () => BlackoutCommand.Execute(null),
                                   (device, side) => AudioDevices.PlayTestSound(device, side));
        // A device opened a timeline: the editor shows it, and the transport works it.
        _control.OpenRequested += id =>
        {
            if (_session.Project.Timelines.FirstOrDefault(t => t.Id == id) is not { } timeline) return;
            Timeline.OpenTimeline(timeline);
            Timeline.Tree.Reveal(timeline);
        };
        _serial = services.SerialInputs ?? new SerialInputs(logger);
        _inputSetup = new LocalInputSetup(session, _serial, settings, SaveSettingsAsync,
            // Only adapters that have their port open: one that's missing keeps the name of the port it was last on, and
            // another device may have that name now (the DMX output checks the adapter's serial number, so it won't take it).
            () => outputs.Services.Where(s => s.Status == DmxOutputStatus.Connected && s.Device.Port is not null).Select(s => (s.Universe, s.Device.Port!)));
        Inputs = new InputsViewModel(session, _inputSetup, Buttons);
        _inputSetup.Changed += () => _triggers.SerialListening = _inputSetup.State.Devices.Any(d => d.IsListening);
        BottomWorkspace = new BottomWorkspaceViewModel(Playback, Inputs.Monitor, Lighting, Diagnostics, settings.Panels, offersPlaying: false);
        // Both come on a port's reading thread, and are handled on this one. A command is an input like a key.
        _serial.CommandReceived += (device, command) => Dispatcher.UIThread.Post(() =>
        {
            _inputSetup.OnCommand(device, command, DateTimeOffset.Now);   // shown on its card
            _triggers.Command(device, command);
        });
        _serial.StatusChanged += _ => Dispatcher.UIThread.Post(_inputSetup.Refresh);
        _audioSetup = new LocalAudioSetup(services.Audio, session, settings, SaveSettingsAsync, services.Thread);
        Remote = new RemoteViewModel(new AppRemoteHost(session, ShowPlayback, Playback, _control, () => WarningsSnapshot, AudioDevices, () => IsDirty, () => _session.FilePath is null || _session.IsSingleFileShow, local.Library, _outputSetup, local.Levels,
                                                       _triggers, _inputSetup, _audioSetup, _diagnostics),
                                     settings.Remote, SaveSettingsAsync, logger);
        Audio = new AudioViewModel(_audioSetup, AudioDevices);
        _ = AudioDevices.RefreshAsync();
        Guide = new SetupGuideViewModel(session, settings, page => CurrentPage = page, () =>
        {
            CurrentPage = Page.Timeline;
            if (Timeline.Tracks.FirstOrDefault(t => t.IsLighting) is { } lighting) Timeline.EditChannels(lighting);
        });
        _session.Document.Changed += OnDocumentChanged;
        _files.AfterShowReplaced = AfterShowReplacedAsync;
        _files.Changed += () =>
        {
            IsDirty = _files.IsDirty;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ShowFile));
        };
        _files.Saved += Timeline.RefreshTitles;
        _files.RecentChanged += RefreshRecentShows;
        _files.Renamed += Remote.ShowReplaced;   // devices show the new name
        _files.Status += message => StatusMessage = message;
        _files.Problem += ReportError;

        _unsavedChanges = new UnsavedChangesGuard(() => _files.IsDirty, () => ShowName, SaveShowAsync, savePrompt);
        _tick = new DispatcherTimer(TickInterval, DispatcherPriority.Background, (_, _) => Tick());
        FollowPanelWidths();
    }

    /// <summary>This computer's own versions of the services the screens use.</summary>
    sealed record LocalServices(ShellServices Shell, ShowPlayback Playback, LocalOutputSetup OutputSetup, LocalLiveLevels Levels,
                                LocalFixtureLibrary Library, SettingsSaver Saver, ShowTriggers Triggers)
    {
        public static LocalServices For(ShowServices services, ILogger logger)
        {
            var saver = new SettingsSaver(services.Settings, services.Paths);
            var outputSetup = new LocalOutputSetup(services.Session, services.Outputs, services.Settings, saver.SaveAsync, services.AdapterFinder);
            var levels = new LocalLiveLevels(services.Session.Engine, services.Outputs.Find);
            var library = new LocalFixtureLibrary(services.Session, services.Paths, services.Settings, logger);
            var playback = new ShowPlayback(services.Session, services.Playback, services.Audio, logger);
            var triggers = new ShowTriggers(services.Session, playback, () => Blackout(services));
            var shell = new ShellServices(playback, library, outputSetup, levels, new AudioDeviceList(services.Audio), new LocalTriggers(triggers));
            return new(shell, playback, outputSetup, levels, library, saver, triggers);
        }
    }

    /// <summary>Saves this computer's settings. A problem is reported in the status bar.</summary>
    sealed class SettingsSaver(MachineSettings settings, AppPaths paths)
    {
        public Action<string>? Problem { get; set; }

        public async Task SaveAsync()
        {
            try
            {
                await MachineSettingsStore.SaveAsync(settings, paths);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Problem?.Invoke($"Couldn't save machine settings: {ex.Message}");
            }
        }
    }

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

    /// <summary>Before the window closes: offers to save unsaved changes. False means the user cancelled closing.</summary>
    public Task<bool> ConfirmCloseAsync() => _unsavedChanges.ConfirmAsync("closing");

    /// <summary>Playback on this computer (the show computer): what the screens and the web remote ask to play.</summary>
    public ShowPlayback ShowPlayback { get; }

    public RemoteViewModel Remote { get; }
    public AudioViewModel Audio { get; }
    public SetupGuideViewModel Guide { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public InputsViewModel Inputs { get; }
    readonly SerialInputs _serial;
    readonly LocalInputSetup _inputSetup;
    readonly ShowTriggers _triggers;
    readonly LocalAudioSetup _audioSetup;
    readonly LocalDiagnostics _diagnostics;
    public PerformanceMeterViewModel Performance { get; }

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
        await _outputSetup.ApplyAsync();
        Inputs.RefreshPortsCommand.Execute(null);
        _inputSetup.Apply();   // after the DMX outputs, so a port they use isn't taken
        _tick.Start();
        if (RecentShows.FirstOrDefault() is { Exists: true } last) await OpenPathAsync(last.Path);
        await Remote.StartIfEnabledAsync();
        await Library.LoadAsync();
    }

    /// <summary>Stops output (each universe sends its blackout first) within the outputs' stop timeout.</summary>
    public async Task ShutdownAsync()
    {
        _tick.Stop();
        await Remote.DisposeAsync();   // devices are told it's going
        // Order matters: stop the show (audio stops, timeline releases the lights), stop the playback thread, close
        // audio, then stop DMX outputs, which send their blackout last.
        _services.Playback.StopAllNow();
        await Task.Delay(PlaybackLoop.Interval * 3);
        _services.PlaybackLoop.Stop(TimeSpan.FromSeconds(2));
        _services.Audio.Dispose();
        _serial.Dispose();
        bool clean = await _outputs.StopAllAsync();
        _logger.LogInformation(clean ? "Shut down cleanly." : "Shut down; an output didn't stop in time.");
    }

    /// <summary>Stops playback and sets every manual level to 0 (Blackout, or a button's Blackout action).</summary>
    static void Blackout(ShowServices services)
    {
        services.Playback.StopAllNow();   // otherwise the timelines would put their levels straight back
        services.Session.Engine.Blackout();
    }

    protected override void BlackoutShow()
    {
        Blackout(_services);
        StatusMessage = "Blackout: playback stopped and every manual level set to 0.";
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

    Task SaveSettingsAsync() => _settingsSaver.SaveAsync();

    async Task AfterShowReplacedAsync()
    {
        Lighting.Reload();
        Playback.Reset();    // the old show's timelines stop at once
        Timeline.Reload();   // focuses the new show's first timeline
        _triggers.Reload();   // every trigger ready, and armed
        Buttons.Reload();
        _inputSetup.Reload();   // the new show's input devices, on this computer's ports
        Inputs.Reload();
        Remote.ShowReplaced();
        Guide.Reset();
        ShowEditor();   // (closing the show goes back to Home after this)
        await _outputSetup.ApplyAsync();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ShowFile));

    }

    /// <summary>A remote edit changed the patch: validate it again, refresh setup screens and apply universe outputs.</summary>
    void OnDocumentChanged(GhostShow.Core.Editing.ShowChange change)
    {
        MarkDirty();
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
        _triggers.Tick();   // re-arming, and firing again while held (the server's, run from here while the desktop hosts it)
        base.Tick();

        _tickCount++;
        if (_tickCount % 15 == 0) Performance.Update();   // twice a second, so the meter moves like one
        if (_tickCount % 30 != 0) return;
        _outputSetup.Tick();
        _inputSetup.Tick();
        Inputs.Tick();
        if (CurrentPage == Page.Diagnostics || _diagnostics.IsWatched) _diagnostics.Refresh();   // here, or on a device
        if (CurrentPage == Page.Audio || _audioSetup.IsWatched) _audioSetup.Refresh();   // here, or on a device
        // Open the devices audio tracks are routed to before they're needed (cheap when nothing changed).
        _services.Audio.UseOutputs(_session.Project.Timelines.SelectMany(t => t.Tracks)
            .Where(t => t.Kind == TrackKind.Audio && !string.IsNullOrWhiteSpace(t.AudioDevice)).Select(t => t.AudioDevice!));
        RefreshWarnings();
        Guide.Refresh();
    }

    void RefreshWarnings() => ShowWarnings(GhostShow.Server.ShowWarnings.Collect(_services, _inputSetup));
}

/// <summary>The Home page's place in the main view (HomeView shows it, on the show computer only).</summary>
public sealed record HomePageViewModel(MainViewModel Main);
