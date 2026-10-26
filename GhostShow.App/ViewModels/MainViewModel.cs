using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Diagnostics;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Storage;
using GhostShow.Dmx;
using Microsoft.Extensions.Logging;

namespace GhostShow.App.ViewModels;

/// <summary>Tabs, in the order MainWindow lists them.</summary>
public enum Page { Console, Outputs, Fixtures, Control, Channels, Monitor, Diagnostics }

/// <summary>
/// The main window: file commands, navigation, the console page, and the UI refresh tick that keeps every screen
/// up to date. The tick runs at 30 Hz on the UI thread, independent of DMX output timing; screens only read the
/// engine and output services, which are thread-safe and never block on I/O.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    static readonly FilePickerFileType ShowFiles = new("GhostShow show") { Patterns = ["*.json"] };
    static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(33);

    readonly ShowSession _session;
    readonly OutputManager _outputs;
    readonly MachineSettings _settings;
    readonly AppPaths _paths;
    readonly IStorageProvider _storage;
    readonly ILogger _logger;
    readonly DispatcherTimer _tick;
    int _tickCount;

    public MainViewModel(ShowSession session, OutputManager outputs, MachineSettings settings, AppPaths paths,
                         IStorageProvider storage, RecentEventsLog log, UiResponsivenessProbe probe, ILogger<MainViewModel> logger)
    {
        _session = session;
        _outputs = outputs;
        _settings = settings;
        _paths = paths;
        _storage = storage;
        _logger = logger;

        Outputs = new OutputsViewModel(session, outputs, settings, SaveSettingsAsync, MarkDirty);
        Fixtures = new FixtureSetupViewModel(session, MarkDirty);
        Control = new FixtureControlViewModel(session, ReportError);
        Channels = new ChannelsViewModel(session.Engine, ReportError);
        Monitor = new MonitorViewModel(session.Engine, outputs.Find);
        Diagnostics = new DiagnosticsViewModel(outputs, probe, log);

        _tick = new DispatcherTimer(TickInterval, DispatcherPriority.Background, (_, _) => Tick());
    }

    public OutputsViewModel Outputs { get; }
    public FixtureSetupViewModel Fixtures { get; }
    public FixtureControlViewModel Control { get; }
    public ChannelsViewModel Channels { get; }
    public MonitorViewModel Monitor { get; }
    public DiagnosticsViewModel Diagnostics { get; }

    public string Title => $"GhostShow — {(_session.FilePath is null ? _session.Project.Name : Path.GetFileName(_session.FilePath))}{(IsDirty ? " *" : "")}";

    public string ShowFile => _session.FilePath ?? "Not saved yet";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))]
    public partial bool IsDirty { get; set; }

    [ObservableProperty]
    public partial int SelectedPage { get; set; }

    /// <summary>The universe the monitor and channel faders show.</summary>
    [ObservableProperty]
    public partial int ActiveUniverse { get; set; }

    public ObservableCollection<int> UniverseNumbers { get; } = new();

    [ObservableProperty]
    public partial string LibraryStatus { get; set; } = "Loading fixture profiles…";

    /// <summary>The latest command error or file problem, shown in the status bar.</summary>
    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    public ObservableCollection<string> Warnings { get; } = new();

    partial void OnActiveUniverseChanged(int value) => Channels.SetUniverse(value);

    /// <summary>Refresh a page as soon as it's shown rather than waiting for its next tick.</summary>
    partial void OnSelectedPageChanged(int value)
    {
        Outputs.Tick();
        if ((Page)value == Page.Diagnostics) Diagnostics.Tick();
        Tick();
    }

    /// <summary>Starts output and loads the full profile library in the background.</summary>
    public async Task StartAsync(string? settingsError)
    {
        if (settingsError is not null) ReportError(settingsError);
        _session.Applied += OnApplied;
        OnApplied();
        await Outputs.ApplyOutputsAsync();
        _tick.Start();
        await LoadProfileLibraryAsync();
    }

    /// <summary>Stops output (each universe sends its blackout first) within the outputs' stop timeout.</summary>
    public async Task ShutdownAsync()
    {
        _tick.Stop();
        bool clean = await _outputs.StopAllAsync();
        _logger.LogInformation(clean ? "Shut down cleanly." : "Shut down; an output didn't stop in time.");
    }

    [RelayCommand]
    void Blackout()
    {
        _session.Engine.Blackout();
        StatusMessage = "Blackout: every channel of every universe set to 0.";
    }

    [RelayCommand]
    void GoTo(Page page) => SelectedPage = (int)page;

    [RelayCommand]
    async Task New()
    {
        _session.New();
        await AfterShowReplacedAsync();
    }

    [RelayCommand]
    async Task Open()
    {
        var files = await _storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open show", FileTypeFilter = [ShowFiles] });
        if (files is not [var file] || file.TryGetLocalPath() is not { } path) return;

        var result = await _session.OpenAsync(path);
        if (result.Project is null)
        {
            ReportError(result.Error!);
            return;
        }
        if (_settings.AdoptMigratedOutputs(result.MigratedOutputs) > 0)
        {
            await SaveSettingsAsync();
            StatusMessage = "This show came from an older version: its output assignments were moved to this machine's settings.";
        }
        await AfterShowReplacedAsync();
    }

    [RelayCommand]
    async Task Save()
    {
        if (_session.FilePath is null) await SaveAs();
        else await WriteShowAsync(_session.FilePath);
    }

    [RelayCommand]
    async Task SaveAs()
    {
        var file = await _storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save show",
            SuggestedFileName = $"{_session.Project.Name}.json",
            DefaultExtension = "json",
            FileTypeChoices = [ShowFiles],
        });
        if (file?.TryGetLocalPath() is { } path) await WriteShowAsync(path);
    }

    async Task WriteShowAsync(string path)
    {
        try
        {
            await _session.SaveAsync(path);
            IsDirty = false;
            StatusMessage = $"Saved {Path.GetFileName(path)}.";
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ShowFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportError($"Couldn't save {Path.GetFileName(path)}: {ex.Message}. The previous file is unchanged.");
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

    async Task LoadProfileLibraryAsync()
    {
        var library = await Task.Run(() => FixtureProfileLoader.Load(_paths, _settings));
        _session.UseProfiles(library);
        Fixtures.Reload();
        LibraryStatus = $"{library.Profiles.Count} fixture profiles" +
                        (library.LoadErrors.Count > 0 ? $"; {library.LoadErrors.Count} files couldn't be read" : "");
        foreach (var error in library.LoadErrors.Take(20)) _logger.LogWarning("Fixture profile {Error}", error);
        _logger.LogInformation("Loaded {Count} fixture profiles.", library.Profiles.Count);
    }

    async Task AfterShowReplacedAsync()
    {
        IsDirty = false;
        Fixtures.Reload();
        Outputs.Reload();
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
            OnPropertyChanged(nameof(ActiveUniverse));
        }
    }

    void MarkDirty() => IsDirty = true;

    void ReportError(string message)
    {
        StatusMessage = message;
        _logger.LogWarning("{Message}", message);
    }

    /// <summary>Screens refresh only while visible; slow-changing status refreshes once a second.</summary>
    void Tick()
    {
        switch ((Page)SelectedPage)
        {
            case Page.Monitor: Monitor.Tick(ActiveUniverse); break;
            case Page.Channels: Channels.Tick(); break;
            case Page.Control: Control.Tick(); break;
        }

        if (++_tickCount % 30 != 0) return;
        Outputs.Tick();
        if ((Page)SelectedPage == Page.Diagnostics) Diagnostics.Tick();
        RefreshWarnings();
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

        if (warnings.SequenceEqual(Warnings)) return;
        Warnings.Clear();
        foreach (var warning in warnings) Warnings.Add(warning);
    }
}
