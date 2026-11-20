using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Dmx;
using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Playback;

namespace GhostShow.App.ViewModels;

/// <summary>
/// The services the screens work through, each with a version for the show computer and one for a device's browser
/// (see docs/plan-shared-view.md). <paramref name="History"/>: where edits go when they aren't simply the show's own
/// undo history (a browser's go to the show computer); null on the show computer.
/// </summary>
public sealed record ShellServices(IPlayback Playback, IFixtureLibrary Library, IOutputSetup OutputSetup, ILiveLevels Levels,
                                   AudioDeviceList AudioDevices, IEditHistory? History = null);

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
        Buttons = new ButtonsViewModel(session, Playback, Blackout, MarkDirty);
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

    /// <summary>
    /// The screens that move while the show plays: the transport, lit buttons, the lighting screens. The host calls it
    /// as often as the screens should refresh, and adds its own.
    /// </summary>
    protected virtual void Tick()
    {
        Playback.Tick();   // the toolbar transport shows on every screen
        Buttons.Tick();    // buttons light while what they run plays
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

    volatile string[] _warningsSnapshot = [];

    /// <summary>The warnings, for other threads (the web remote's status).</summary>
    internal IReadOnlyList<string> WarningsSnapshot => _warningsSnapshot;

    /// <summary>Shows these warnings (the list only changes when they do).</summary>
    protected void ShowWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.SequenceEqual(Warnings)) return;
        _warningsSnapshot = [.. warnings];
        Warnings.Clear();
        foreach (var warning in warnings) Warnings.Add(warning);
    }
}
