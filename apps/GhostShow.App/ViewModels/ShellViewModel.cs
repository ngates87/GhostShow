using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Dmx;
using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Playback;

namespace GhostShow.App.ViewModels;

/// <summary>A show in the Home screen's recent list.</summary>
/// <param name="Path">The show folder, or an older single-file show.</param>
public sealed record RecentShowRow(string Path, string Name, string Location, bool Exists)
{
    public string Note => Exists ? "" : "Not found (moved, renamed or on a drive that isn't connected)";
}

/// <summary>
/// The services the screens work through, each with a version for the show computer and one for a device's browser
/// (see docs/plan-shared-view.md). <paramref name="History"/>: where edits go when they aren't simply the show's own
/// undo history (a browser's go to the show computer); null on the show computer. <paramref name="Triggers"/>: the show
/// computer's triggers, where they run (its server's, in-process there).
/// </summary>
public sealed record ShellServices(IPlayback Playback, IFixtureLibrary Library, IOutputSetup OutputSetup, ILiveLevels Levels,
                                   AudioDeviceList AudioDevices, IRemoteTriggers Triggers, IEditHistory? History = null);

/// <summary>
/// What the main view shows and does, the same on the show computer and in a browser: the screens, the sections and
/// pages, status messages and warnings, and Blackout. Each host adds its own part: <see cref="MainViewModel"/> on the
/// show computer (saving, files, sound, inputs, diagnostics, the web remote), the browser's over the connection. Things
/// only a host knows (how to save, what Blackout does, picking a sound) are the abstract members below.
/// </summary>
public abstract partial class ShellViewModel : ObservableObject
{
    protected ShellViewModel(ShowSession session, ShellServices services)
    {
        Session = session;
        Services = services;
        Library = new ProfileLibraryViewModel(services.Library);
        Lighting = new LightingViewModel(session, services.Levels,
                                         new OutputsViewModel(session, services.OutputSetup, ReportError, services.History),
                                         new FixtureSetupViewModel(session, Library, services.History), ReportError);
        Lighting.PageChanged += () =>
        {
            OnPropertyChanged(nameof(CurrentPage));
            OnPageShown();
        };
        Playback = new PlaybackViewModel(services.Playback);
        AudioDevices = services.AudioDevices;
        Timeline = new TimelineEditorViewModel(session, Playback, PickAudioFileAsync, MarkDirty, AudioDevices, services.History);
        Buttons = new ButtonsViewModel(session, Playback, MarkDirty, services.Triggers);
        Timeline.TriggersOf = Buttons.TriggersFor;
        Timeline.ShowFixtures = () => CurrentPage = Page.Fixtures;
        Timeline.Notice += message => StatusMessage = message;
    }

    protected ShowSession Session { get; }
    protected ShellServices Services { get; }

    public PlaybackViewModel Playback { get; }
    public TimelineEditorViewModel Timeline { get; }
    public ButtonsViewModel Buttons { get; }
    public AudioDeviceList AudioDevices { get; }
    public ProfileLibraryViewModel Library { get; }
    public LightingViewModel Lighting { get; }
    public OutputsViewModel Outputs => Lighting.Outputs;
    public FixtureSetupViewModel Fixtures => Lighting.Fixtures;

    /// <summary>The panes under the timeline. The host makes it, since what they can show depends on where it runs.</summary>
    public BottomWorkspaceViewModel BottomWorkspace { get; protected set; } = null!;

    // ---- what only the host knows ----

    /// <summary>The show's name, as the title shows it.</summary>
    protected abstract string ShowName { get; }

    /// <summary>The show was edited (a change the show's document doesn't see, such as a timeline's own setting).</summary>
    protected abstract void MarkDirty();

    /// <summary>Stops playback and sets every manual level to 0.</summary>
    protected abstract void BlackoutShow();

    /// <summary>Asks for a sound file: its path on the show computer, or null if none was chosen.</summary>
    protected abstract Task<string?> PickAudioFileAsync();

    /// <summary>
    /// This is the show computer (the desktop app), not a device's browser. Things only the show computer has are shown
    /// only there: its files and folders, its own windows, trigger keys.
    /// </summary>
    public abstract bool IsShowComputer { get; }

    /// <summary>
    /// Whoever is using this may change the show: always on the show computer; on a device, unless it's paired as an
    /// operator (who runs the show but can't change it).
    /// </summary>
    [ObservableProperty]
    public partial bool IsEditor { get; protected set; } = true;

    // ---- the File menu: the host does the work (folders and pickers on the show computer, names in the shows folder from a device) ----

    [RelayCommand]
    Task New() => NewShowAsync();

    [RelayCommand]
    Task Open() => OpenShowAsync();

    [RelayCommand]
    Task Save() => SaveShowAsync();

    protected abstract Task NewShowAsync();
    protected abstract Task OpenShowAsync();
    protected abstract Task SaveShowAsync();

    // The show computer's own (shown only there; see IsShowComputer).
    [RelayCommand] protected virtual Task SaveAs() => Task.CompletedTask;
    [RelayCommand] protected virtual Task ImportVenueMagic() => Task.CompletedTask;
    [RelayCommand] protected virtual Task OpenRecent(RecentShowRow? row) => Task.CompletedTask;
    [RelayCommand] protected virtual Task OpenOldShow() => Task.CompletedTask;
    [RelayCommand] protected virtual Task CollectAudio() => Task.CompletedTask;
    [RelayCommand] protected virtual Task CloseShow() => Task.CompletedTask;

    /// <summary>A device's: gives the show another name (on the show computer, its folder is renamed).</summary>
    [RelayCommand] protected virtual Task Rename() => Task.CompletedTask;

    /// <summary>Shows opened or saved on the show computer, most recent first (empty on a device).</summary>
    public ObservableCollection<RecentShowRow> RecentShows { get; } = new();

    /// <summary>File → Exit: the window closes as if its close button was pressed (offering to save first).</summary>
    public event Action? ExitRequested;

    [RelayCommand]
    void Exit() => ExitRequested?.Invoke();

    /// <summary>View → Remote Access…, and the menu bar's indicator: the host opens its window.</summary>
    public event Action? RemoteAccessRequested;

    [RelayCommand]
    void ShowRemoteAccess() => RemoteAccessRequested?.Invoke();

    /// <summary>Help → About GhostShow: the host opens it.</summary>
    public event Action? AboutRequested;

    [RelayCommand]
    void ShowAbout() => AboutRequested?.Invoke();

    // ---- what each host shows in the frame's places (see the data templates in its App.axaml) ----

    /// <summary>Home: shown while no show is open (never on a device: the show computer always has one).</summary>
    public virtual object? HomePage => null;

    public abstract object AudioPage { get; }
    public abstract object InputsPage { get; }
    public abstract object DiagnosticsPage { get; }

    /// <summary>The show's buttons, beside the timeline.</summary>
    public abstract object ButtonsPart { get; }

    /// <summary>Getting started, above the pages (null: none).</summary>
    public virtual object? GuidePart => null;

    /// <summary>In the menu bar, beside the warnings (null: nothing).</summary>
    public virtual object? MenuBarPart => null;

    /// <summary>At the right of the status bar (null: nothing).</summary>
    public virtual object? StatusPart => null;

    // ---- the frame ----

    public string Title => IsShowOpen ? $"GhostShow — {ShowName}{(IsDirty ? " *" : "")}" : "GhostShow";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))]
    public partial bool IsDirty { get; set; }

    /// <summary>
    /// A show is open: the editor and setup screens are shown. When not (at first start, or after Close Show), the
    /// Home page is: new, open, recent shows.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage), nameof(Title))]
    public partial bool IsShowOpen { get; protected set; }

    partial void OnIsShowOpenChanged(bool value) => OnPageShown();

    /// <summary>Index of the selected top-level tab (see <see cref="Section"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    public partial int SelectedSection { get; set; }

    partial void OnSelectedSectionChanged(int value) => OnPageShown();

    /// <summary>The screen on display; drives which screens refresh.</summary>
    public Page CurrentPage
    {
        get => !IsShowOpen ? Page.Home : (Section)SelectedSection switch
        {
            Section.Timeline => Page.Timeline,
            Section.Lighting => Lighting.CurrentPage,
            Section.Audio => Page.Audio,
            Section.Inputs => Page.InputMonitor,
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
                    Lighting.CurrentPage = value;
                    SelectedSection = (int)Section.Lighting;
                    break;
                case Page.Audio:
                    SelectedSection = (int)Section.Audio;
                    break;
                case Page.InputMonitor:
                    SelectedSection = (int)Section.Inputs;
                    break;
                default:
                    SelectedSection = (int)Section.Diagnostics;
                    break;
            }
        }
    }

    /// <summary>After opening or starting a show: straight to the timeline editor, where most of the work is.</summary>
    public void ShowEditor()
    {
        IsShowOpen = true;
        CurrentPage = Page.Timeline;
    }

    [RelayCommand]
    void GoTo(Page page) => CurrentPage = page;

    /// <summary>
    /// A page was shown: refresh it now rather than on its next tick. The host adds its own pages' refreshes, and
    /// calls this first.
    /// </summary>
    protected virtual void OnPageShown() => Lighting.IsOnScreen = CurrentPage is >= Page.DmxSetup and <= Page.Monitor;

    /// <summary>Whether the keyboard here fires triggers: only the show computer's does (decided 2026-11-07).</summary>
    public bool KeysFireTriggers => IsShowComputer;

    /// <summary>
    /// The screens that move while the show plays: the transport, lit buttons, the lighting screens. The host calls it
    /// as often as the screens should refresh, and adds its own.
    /// </summary>
    protected virtual void Tick()
    {
        Playback.Tick();   // the toolbar transport shows on every screen
        Buttons.Tick();    // buttons light while what they run plays
        Timeline.Tree.ShowRunning(Playback.Show.Running);   // ▶ by each timeline playing
        Lighting.Tick();
    }

    [RelayCommand]
    void Blackout() => BlackoutShow();

    // ---- status and warnings ----

    /// <summary>The latest command error or file problem, shown in the status bar.</summary>
    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    /// <summary>Something went wrong: said in the status bar.</summary>
    protected virtual void ReportError(string message) => StatusMessage = message;

    /// <summary>What needs attention: patch errors, failing outputs, missing devices.</summary>
    public ObservableCollection<string> Warnings { get; } = new();

    /// <summary>Shows these warnings (the list only changes when they do).</summary>
    protected void ShowWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.SequenceEqual(Warnings)) return;
        Warnings.Clear();
        foreach (var warning in warnings) Warnings.Add(warning);
    }
}
