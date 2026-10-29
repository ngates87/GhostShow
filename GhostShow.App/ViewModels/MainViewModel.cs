using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Diagnostics;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Playback;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;
using GhostShow.Dmx;
using Microsoft.Extensions.Logging;

namespace GhostShow.App.ViewModels;

/// <summary>Top-level tabs while a show is open, in the order MainWindow lists them.</summary>
public enum Section { Timeline, Lighting, Audio, Diagnostics }

/// <summary>
/// Every screen. Home is shown when no show is open. Lighting's screens (DMX setup to Monitor) are in their tab order.
/// </summary>
public enum Page { Home, Timeline, DmxSetup, Fixtures, Control, Channels, Monitor, Audio, Diagnostics }

/// <summary>
/// The main window: file commands, navigation, the console page, and the UI refresh tick that keeps every screen
/// up to date. The tick runs at 30 Hz on the UI thread, independent of DMX output timing; screens only read the
/// engine and output services, which are thread-safe and never block on I/O.
/// </summary>
/// <summary>A show in the Home screen's recent list.</summary>
/// <param name="Path">The show folder, or an older single-file show.</param>
public sealed record RecentShowRow(string Path, string Name, string Location, bool Exists)
{
    public string Note => Exists ? "" : "Not found (moved, renamed or on a drive that isn't connected)";
}

public sealed partial class MainViewModel : ObservableObject
{
    static readonly FilePickerFileType OldShowFiles = new("GhostShow single-file show (older versions)") { Patterns = ["*.json"] };
    static readonly FilePickerFileType AudioFiles = new("Audio (WAV, MP3)") { Patterns = ["*.wav", "*.mp3", "*.WAV", "*.MP3"] };
    static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(33);

    readonly AppServices _services;
    readonly ShowSession _session;
    readonly OutputManager _outputs;
    readonly MachineSettings _settings;
    readonly AppPaths _paths;
    readonly IStorageProvider _storage;
    readonly ILogger _logger;
    readonly DispatcherTimer _tick;
    int _tickCount;

    public MainViewModel(AppServices services, IStorageProvider storage, ISaveChangesPrompt savePrompt, ILogger<MainViewModel> logger)
    {
        _services = services;
        var (session, outputs, settings, paths) = (services.Session, services.Outputs, services.Settings, services.Paths);
        _session = session;
        _outputs = outputs;
        _settings = settings;
        _paths = paths;
        _storage = storage;
        _logger = logger;

        Outputs = new OutputsViewModel(session, outputs, settings, SaveSettingsAsync, MarkDirty);
        Library = new ProfileLibraryViewModel(session, paths, settings, logger);
        Fixtures = new FixtureSetupViewModel(session, Library, MarkDirty);
        Control = new FixtureControlViewModel(session, ReportError);
        Channels = new ChannelsViewModel(session.Engine, ReportError);
        Monitor = new MonitorViewModel(session.Engine, outputs.Find);
        Playback = new PlaybackViewModel(services, logger);
        Diagnostics = new DiagnosticsViewModel(services, Playback);
        Performance = new PerformanceMeterViewModel(() => services.PlaybackLoop.Timing, () => services.Probe.Snapshot(last: 20).P95Ms);
        AudioDevices = new AudioDeviceList(services.Audio);
        Timeline = new TimelineEditorViewModel(session, Playback, PickAudioFileAsync, MarkDirty, AudioDevices);
        Buttons = new ButtonsViewModel(session, Playback, Blackout, MarkDirty);
        Audio = new AudioViewModel(services.Audio, AudioDevices, session, settings, SaveSettingsAsync);
        _ = AudioDevices.RefreshAsync();
        Guide = new SetupGuideViewModel(session, settings, page => CurrentPage = page, () =>
        {
            CurrentPage = Page.Timeline;
            if (Timeline.Tracks.FirstOrDefault(t => t.IsLighting) is { } lighting) Timeline.EditChannels(lighting);
        });
        Timeline.ShowFixtures = () => CurrentPage = Page.Fixtures;
        Timeline.Notice += message => StatusMessage = message;

        _unsavedChanges = new UnsavedChangesGuard(() => IsDirty, () => ShowName, Save, savePrompt);
        _tick = new DispatcherTimer(TickInterval, DispatcherPriority.Background, (_, _) => Tick());
    }

    readonly UnsavedChangesGuard _unsavedChanges;

    string ShowName => _session.ShowFolder is { } folder && !_session.IsSingleFileShow ? Path.GetFileName(folder)
                     : _session.FilePath is { } file ? Path.GetFileName(file)
                     : _session.Project.Name;

    /// <summary>Before the window closes: offers to save unsaved changes. False means the user cancelled closing.</summary>
    public Task<bool> ConfirmCloseAsync() => _unsavedChanges.ConfirmAsync("closing");

    public PlaybackViewModel Playback { get; }
    public TimelineEditorViewModel Timeline { get; }
    public ButtonsViewModel Buttons { get; }
    public AudioViewModel Audio { get; }
    public AudioDeviceList AudioDevices { get; }
    public SetupGuideViewModel Guide { get; }
    public ProfileLibraryViewModel Library { get; }
    public OutputsViewModel Outputs { get; }
    public FixtureSetupViewModel Fixtures { get; }
    public FixtureControlViewModel Control { get; }
    public ChannelsViewModel Channels { get; }
    public MonitorViewModel Monitor { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public PerformanceMeterViewModel Performance { get; }

    public string Title => IsShowOpen ? $"GhostShow — {ShowName}{(IsDirty ? " *" : "")}" : "GhostShow";

    public string ShowFile => _session.IsSingleFileShow ? $"{_session.FilePath} (older single-file show: Save will ask for a folder)"
                            : _session.ShowFolder ?? "Not saved yet";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))]
    public partial bool IsDirty { get; set; }

    /// <summary>
    /// A show is open: the editor and setup screens are shown. When not (at first start, or after Close Show), the
    /// Home page is: new, open, recent shows.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage), nameof(Title))]
    public partial bool IsShowOpen { get; private set; }

    partial void OnIsShowOpenChanged(bool value) => OnPageShown();

    /// <summary>Index of the selected top-level tab (see <see cref="Section"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    public partial int SelectedSection { get; set; }

    /// <summary>Index of the selected tab within Lighting.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    public partial int SelectedLightingPage { get; set; }

    /// <summary>The screen on display; drives which screens refresh.</summary>
    public Page CurrentPage
    {
        get => !IsShowOpen ? Page.Home : (Section)SelectedSection switch
        {
            Section.Timeline => Page.Timeline,
            Section.Lighting => Page.DmxSetup + SelectedLightingPage,
            Section.Audio => Page.Audio,
            _ => Page.Diagnostics,
        };
        set
        {
            switch (value)
            {
                case Page.Home:
                    break;   // Home is where you are when no show is open; File → Close Show gets there
                case Page.Timeline:
                    SelectedSection = (int)Section.Timeline;
                    break;
                case <= Page.Monitor:
                    SelectedLightingPage = value - Page.DmxSetup;
                    SelectedSection = (int)Section.Lighting;
                    break;
                case Page.Audio:
                    SelectedSection = (int)Section.Audio;
                    break;
                default:
                    SelectedSection = (int)Section.Diagnostics;
                    break;
            }
        }
    }

    /// <summary>The universe the monitor and channel faders show.</summary>
    [ObservableProperty]
    public partial int ActiveUniverse { get; set; } = GhostShow.Core.Model.Project.FirstUniverse;

    public ObservableCollection<int> UniverseNumbers { get; } = new();


    /// <summary>The latest command error or file problem, shown in the status bar.</summary>
    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    public ObservableCollection<string> Warnings { get; } = new();

    // ---- Home screen ----

    /// <summary>Shows opened or saved on this computer, most recent first.</summary>
    public ObservableCollection<RecentShowRow> RecentShows { get; } = new();

    [RelayCommand]
    async Task OpenRecent(RecentShowRow? row)
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
        _settings.RemoveRecentShow(row.Path);
        RefreshRecentShows();
        await SaveSettingsAsync();
    }

    void RefreshRecentShows()
    {
        RecentShows.Clear();
        foreach (var path in _settings.RecentShows)
        {
            bool exists = Directory.Exists(path) ? ShowFolderStore.IsShowFolder(path) : File.Exists(path);
            RecentShows.Add(new RecentShowRow(path, Path.GetFileName(path), Path.GetDirectoryName(path) ?? "", exists));
        }
    }

    /// <summary>After opening or saving: this show goes to the top of the recent list.</summary>
    async Task RememberShowAsync()
    {
        string? path = _session.IsSingleFileShow ? _session.FilePath : _session.ShowFolder;
        if (path is null) return;
        _settings.AddRecentShow(path);
        RefreshRecentShows();
        await SaveSettingsAsync();
    }

    /// <summary>For universe pickers, which briefly select nothing while their list is rebuilt (see PatchMapViewModel.UniverseChoice).</summary>
    public int? ActiveUniverseChoice
    {
        get => ActiveUniverse;
        set { if (value is { } universe) ActiveUniverse = universe; }
    }

    partial void OnActiveUniverseChanged(int value)
    {
        OnPropertyChanged(nameof(ActiveUniverseChoice));
        Channels.SetUniverse(value);
    }

    partial void OnSelectedSectionChanged(int value) => OnPageShown();
    partial void OnSelectedLightingPageChanged(int value) => OnPageShown();

    /// <summary>Refresh a page as soon as it's shown rather than waiting for its next tick.</summary>
    void OnPageShown()
    {
        if (CurrentPage == Page.DmxSetup) _ = Outputs.WatchAdaptersAsync();   // scan as soon as the screen opens
        if (CurrentPage == Page.Home) RefreshRecentShows();   // a recent show may have been moved or reconnected since
        Guide.OnTimeline = CurrentPage == Page.Timeline;
        Guide.Refresh();
        if (CurrentPage == Page.Audio)
        {
            _ = Audio.RefreshDevicesCommand.ExecuteAsync(null);
            Audio.Tick();
        }
        Outputs.Tick();
        if (CurrentPage == Page.Diagnostics) Diagnostics.Tick();
        Tick();
    }

    /// <summary>
    /// Starts output, reopens the show used last (if it's still there; otherwise Home is shown), and loads the full
    /// profile library in the background.
    /// </summary>
    public async Task StartAsync(string? settingsError)
    {
        if (settingsError is not null) ReportError(settingsError);
        _session.Applied += OnApplied;
        OnApplied();
        RefreshRecentShows();
        await Outputs.ApplyOutputsAsync();
        _tick.Start();
        if (RecentShows.FirstOrDefault() is { Exists: true } last) await OpenPathAsync(last.Path);
        await Library.LoadAsync();
    }

    /// <summary>Stops output (each universe sends its blackout first) within the outputs' stop timeout.</summary>
    public async Task ShutdownAsync()
    {
        _tick.Stop();
        // Order matters: stop the show (audio stops, timeline releases the lights), stop the playback thread, close
        // audio, then stop DMX outputs, which send their blackout last.
        _services.Playback.StopAllNow();
        await Task.Delay(PlaybackLoop.Interval * 3);
        _services.PlaybackLoop.Stop(TimeSpan.FromSeconds(2));
        _services.Audio.Dispose();
        bool clean = await _outputs.StopAllAsync();
        _logger.LogInformation(clean ? "Shut down cleanly." : "Shut down; an output didn't stop in time.");
    }

    [RelayCommand]
    void Blackout()
    {
        _services.Playback.StopAllNow();   // otherwise the timelines would put their levels straight back
        _session.Engine.Blackout();
        StatusMessage = "Blackout: playback stopped and every manual level set to 0.";
    }

    [RelayCommand]
    void GoTo(Page page) => CurrentPage = page;

    [RelayCommand]
    async Task New()
    {
        if (!await _unsavedChanges.ConfirmAsync("starting a new show")) return;
        _session.New();
        await AfterShowReplacedAsync();
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
    [RelayCommand]
    async Task ImportVenueMagic()
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
            if (Library.SaveCustom(profile) is { } error) ReportError(error);
        _session.Use(result.Project);
        await AfterShowReplacedAsync();
        IsDirty = true;
        ShowEditor();
        StatusMessage = $"Imported {Path.GetFileName(path)}: {result.Project.Fixtures.Count} fixtures, {result.Profiles.Count} fixture types, "
                        + $"{result.Project.Timelines.Count} timelines"
                        + (result.Notes.Count > 0 ? $"; {result.Notes.Count} notes on what wasn't converted (Diagnostics → recent events)" : "")
                        + ". Save (Ctrl+S) picks its folder.";
        foreach (var note in result.Notes) _logger.LogWarning("Import: {Note}", note);
        ImportNotes = result.Notes;
    }

    /// <summary>Closes the show (offering to save) and returns to Home.</summary>
    [RelayCommand]
    async Task CloseShow()
    {
        if (!IsShowOpen || !await _unsavedChanges.ConfirmAsync("closing the show")) return;
        _session.New();   // an empty show underneath, so the engine and outputs carry on (dark)
        await AfterShowReplacedAsync();
        IsShowOpen = false;
        StatusMessage = "";
    }

    /// <summary>File → Exit: the window closes as if its close button was pressed (offering to save first).</summary>
    public event Action? ExitRequested;

    [RelayCommand]
    void Exit() => ExitRequested?.Invoke();

    /// <summary>After opening or starting a show: straight to the timeline editor, where most of the work is.</summary>
    public void ShowEditor()
    {
        IsShowOpen = true;
        CurrentPage = Page.Timeline;
    }

    /// <summary>Opens a show folder (one with a show.json).</summary>
    [RelayCommand]
    async Task Open()
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
    [RelayCommand]
    async Task OpenOldShow()
    {
        if (!await _unsavedChanges.ConfirmAsync("opening another show")) return;
        var files = await _storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open older single-file show", FileTypeFilter = [OldShowFiles] });
        if (files is [var file] && file.TryGetLocalPath() is { } path) await OpenPathAsync(path);
    }

    async Task OpenPathAsync(string path)
    {
        var result = await _session.OpenAsync(path);
        if (result.Project is null)
        {
            ReportError(result.Error!);
            return;
        }
        await AfterShowReplacedAsync();
        ShowEditor();
        var notes = new List<string>();
        if (_settings.AdoptMigratedOutputs(result.MigratedOutputs) > 0)
        {
            await SaveSettingsAsync();
            notes.Add("Its output assignments were moved to this machine's settings.");
        }
        await RememberShowAsync();
        if (result.IsSingleFileShow) notes.Add("This show is from an older version (a single file). Saving asks for a folder to save it in; the old file isn't changed.");
        if (result.Warnings is { Count: > 0 } warnings) notes.Add($"{warnings.Count} problem(s) opening it: {string.Join(" ", warnings)}");
        StatusMessage = notes.Count > 0 ? string.Join(" ", notes) : $"Opened {ShowName}.";
    }

    [RelayCommand]
    async Task Save()
    {
        if (_session.FilePath is null || _session.IsSingleFileShow) await SaveAs();
        else await WriteShowAsync(folder: null);
    }

    /// <summary>
    /// Saves the show as a folder. Pick where: an empty folder (e.g. one just made in the picker) is used as it is;
    /// anywhere else, a new folder named after the show is made inside it, so a show is never mixed into another
    /// folder's files.
    /// </summary>
    [RelayCommand]
    async Task SaveAs()
    {
        var picked = await _storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Save show: choose a folder for it (an empty one, or a place to make it in)",
        });
        if (picked is not [var location] || location.TryGetLocalPath() is not { } path) return;
        await WriteShowAsync(ShowFolderFor(path));
    }

    string ShowFolderFor(string picked)
    {
        bool usable = !Directory.EnumerateFileSystemEntries(picked).Any()
                      || (_session.ShowFolder is { } current && !_session.IsSingleFileShow && Path.GetFullPath(picked) == Path.GetFullPath(current));
        if (usable) return picked;
        string name = TimelineNames.Sanitize(_session.Project.Name, "Show");
        return Path.Combine(picked, TimelineNames.Unique(name, Directory.EnumerateFileSystemEntries(picked).Select(Path.GetFileName).OfType<string>()));
    }

    async Task WriteShowAsync(string? folder)
    {
        try
        {
            var warnings = await _session.SaveAsync(folder);
            IsDirty = false;
            Timeline.RefreshTitles();
            await RememberShowAsync();
            StatusMessage = warnings.Count == 0 ? $"Saved {_session.ShowFolder}." : $"Saved {_session.ShowFolder}. {string.Join(" ", warnings)}";
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ShowFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportError($"Couldn't save the show: {ex.Message}. Files already saved are complete; nothing was deleted.");
        }
    }

    async Task SaveSettingsAsync()
    {
        try
        {
            await MachineSettingsStore.SaveAsync(_settings, _paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportError($"Couldn't save machine settings: {ex.Message}");
        }
    }

    async Task AfterShowReplacedAsync()
    {
        IsDirty = false;
        Fixtures.Reload();
        Outputs.Reload();
        Playback.Reset();    // the old show's timelines stop at once
        Timeline.Reload();   // focuses the new show's first timeline
        Buttons.Reload();
        Guide.Reset();
        await Outputs.ApplyOutputsAsync();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ShowFile));

    }

    void OnApplied()
    {
        var universes = _session.Engine.Universes;
        if (!universes.SequenceEqual(UniverseNumbers))
        {
            int active = ActiveUniverse;
            UniverseNumbers.Clear();
            foreach (var universe in universes) UniverseNumbers.Add(universe);
            ActiveUniverse = universes.Contains(active) ? active : universes.FirstOrDefault();
            // Pickers bound to ActiveUniverse lost their selection when the list was cleared; re-announce it even if unchanged.
            OnPropertyChanged(nameof(ActiveUniverseChoice));
        }
    }

    void MarkDirty() => IsDirty = true;

    async Task<string?> PickAudioFileAsync()
    {
        var files = await _storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose a sound", FileTypeFilter = [AudioFiles] });
        return files is [var file] ? file.TryGetLocalPath() : null;
    }

    void ReportError(string message)
    {
        StatusMessage = message;
        _logger.LogWarning("{Message}", message);
    }

    /// <summary>Screens refresh only while visible; slow-changing status refreshes once a second.</summary>
    void Tick()
    {
        Playback.Tick();   // the toolbar transport shows on every screen
        Buttons.Tick();    // buttons light while what they run plays
        switch (CurrentPage)
        {
            case Page.Monitor: Monitor.Tick(ActiveUniverse); break;
            case Page.Channels: Channels.Tick(); break;
            case Page.Control: Control.Tick(); break;
        }

        _tickCount++;
        if (_tickCount % 15 == 0) Performance.Update();   // twice a second, so the meter moves like one
        if (_tickCount % 30 != 0) return;
        Outputs.Tick();
        // Every 2 s on the DMX setup screen: notice adapters plugged in or out (cheap; see WatchAdaptersAsync).
        if (CurrentPage == Page.DmxSetup && _tickCount % 60 == 0) _ = Outputs.WatchAdaptersAsync();
        if (CurrentPage == Page.Diagnostics) Diagnostics.Tick();
        if (CurrentPage == Page.Audio) Audio.Tick();
        // Open the devices audio tracks are routed to before they're needed (cheap when nothing changed).
        _services.Audio.UseOutputs(_session.Project.Timelines.SelectMany(t => t.Tracks)
            .Where(t => t.Kind == TrackKind.Audio && !string.IsNullOrWhiteSpace(t.AudioDevice)).Select(t => t.AudioDevice!));
        RefreshWarnings();
        Guide.Refresh();
    }

    void RefreshWarnings()
    {
        var warnings = new List<string>();
        int errors = _session.Patch.Issues.Count(i => i.Severity == PatchIssueSeverity.Error);
        int overlaps = _session.Patch.Issues.Count - errors;
        if (errors > 0) warnings.Add($"{errors} fixture/universe errors: those fixtures aren't output. See Fixtures.");
        if (overlaps > 0) warnings.Add($"{overlaps} patch warnings (e.g. overlapping addresses). See Fixtures.");
        foreach (var service in _outputs.Services.Where(s => s.Status == DmxOutputStatus.Faulted))
            warnings.Add($"Universe {service.Universe}: {service.LastError} Retrying.");
        foreach (var output in _services.Audio.Status.MissingOutputs)
            warnings.Add($"Audio output '{output}' isn't connected: its tracks play on the default output. See Audio.");

        if (warnings.SequenceEqual(Warnings)) return;
        Warnings.Clear();
        foreach (var warning in warnings) Warnings.Add(warning);
    }
}
