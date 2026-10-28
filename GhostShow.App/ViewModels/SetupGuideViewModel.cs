using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Engine;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;

namespace GhostShow.App.ViewModels;

/// <summary>One step of the setup guide: done (✓), or a button that goes where it's done.</summary>
public sealed partial class SetupStepViewModel(string title, string hint, string actionLabel, Action action) : ObservableObject
{
    public string Title { get; } = title;
    public string Hint { get; } = hint;
    public string ActionLabel { get; } = actionLabel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mark))]
    public partial bool IsDone { get; set; }

    public string Mark => IsDone ? "✓" : "○";

    [RelayCommand]
    void Go() => action();
}

/// <summary>
/// "Getting started", at the top of the Timeline screen while a show isn't set up: the order things have to happen in
/// (hardware, fixtures, then a lighting track's channels), each with a button to the screen that does it. Goes away
/// once fixtures are patched and a lighting track has channels, or when dismissed (for this show).
/// </summary>
public sealed partial class SetupGuideViewModel : ObservableObject
{
    readonly ShowSession _session;
    readonly MachineSettings _settings;

    public SetupGuideViewModel(ShowSession session, MachineSettings settings, Action<Page> goTo, Action editChannels)
    {
        _session = session;
        _settings = settings;
        Adapter = new("Connect a DMX adapter", "Optional: skip it to design without hardware (output goes to the simulator).", "DMX setup", () => goTo(Page.DmxSetup));
        Fixtures = new("Patch your fixtures", "Add the lights and effects, with their DMX addresses.", "Fixtures", () => goTo(Page.Fixtures));
        Channels = new("Give a lighting track its channels", "Pick which fixtures the track controls; then add Level Control clips.", "Channels…", editChannels);
        Steps = [Adapter, Fixtures, Channels];
    }

    public SetupStepViewModel Adapter { get; }
    public SetupStepViewModel Fixtures { get; }
    public SetupStepViewModel Channels { get; }
    public IReadOnlyList<SetupStepViewModel> Steps { get; }

    [ObservableProperty]
    public partial bool IsVisible { get; private set; }

    bool _dismissed;

    /// <summary>Whether the Timeline screen is showing (the guide is only shown there).</summary>
    public bool OnTimeline { get; set; }

    [RelayCommand]
    void Dismiss()
    {
        _dismissed = true;
        Refresh();
    }

    /// <summary>A new or newly opened show: show the guide again if it isn't set up.</summary>
    public void Reset()
    {
        _dismissed = false;
        Refresh();
    }

    public void Refresh()
    {
        var project = _session.Project;
        Adapter.IsDone = project.Universes.Any(u => _settings.FindOutput(u.Number) is { } device && device is not SimulatorDeviceConfig);
        Fixtures.IsDone = _session.Patch.Fixtures.Count > 0;
        Channels.IsDone = project.Timelines.SelectMany(t => t.Tracks).Any(t => t.Kind == TrackKind.Lighting && t.Channels.Count > 0);
        IsVisible = OnTimeline && !_dismissed && !(Fixtures.IsDone && Channels.IsDone);
    }
}
