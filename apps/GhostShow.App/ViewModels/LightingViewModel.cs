using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GhostShow.Core.Engine;
using GhostShow.Core.Model;

namespace GhostShow.App.ViewModels;

/// <summary>
/// The Lighting section: its row of screens (DMX setup, Fixtures, Control, Channels, Monitor), the same on the show
/// computer and on a device. Screens only refresh while on show, so whoever hosts this calls <see cref="Tick"/>.
/// </summary>
public sealed partial class LightingViewModel : ObservableObject
{
    readonly ShowSession _session;
    readonly ILiveLevels _levels;
    bool _isOnScreen;

    public LightingViewModel(ShowSession session, ILiveLevels levels, OutputsViewModel outputs, FixtureSetupViewModel fixtures, Action<string> reportError)
    {
        _session = session;
        _levels = levels;
        Outputs = outputs;
        Fixtures = fixtures;
        Control = new FixtureControlViewModel(session, levels, reportError);
        Channels = new ChannelsViewModel(levels, reportError);
        Monitor = new MonitorViewModel(levels);
        session.Applied += OnApplied;
        OnApplied();
    }

    public OutputsViewModel Outputs { get; }
    public FixtureSetupViewModel Fixtures { get; }
    public FixtureControlViewModel Control { get; }
    public ChannelsViewModel Channels { get; }
    public MonitorViewModel Monitor { get; }

    /// <summary>A screen was chosen.</summary>
    public event Action? PageChanged;

    /// <summary>Index of the selected screen in the row of tabs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    public partial int SelectedPage { get; set; }

    public Page CurrentPage
    {
        get => Page.DmxSetup + SelectedPage;
        set => SelectedPage = value - Page.DmxSetup;
    }

    /// <summary>The universe the monitor and channel faders show.</summary>
    [ObservableProperty]
    public partial int ActiveUniverse { get; set; } = Project.FirstUniverse;

    public ObservableCollection<int> UniverseNumbers { get; } = new();

    /// <summary>For universe pickers, which briefly select nothing while their list is rebuilt (see PatchMapViewModel.UniverseChoice).</summary>
    public int? ActiveUniverseChoice
    {
        get => ActiveUniverse;
        set { if (value is { } universe) ActiveUniverse = universe; }
    }

    /// <summary>The Lighting section is the one showing.</summary>
    public bool IsOnScreen
    {
        get => _isOnScreen;
        set
        {
            if (value == _isOnScreen) return;
            _isOnScreen = value;
            TellWhatIsOnScreen();
        }
    }

    /// <summary>Another show arrived: the screens that keep rows rebuild them.</summary>
    public void Reload()
    {
        Fixtures.Reload();
        Outputs.Reload();
    }

    public void Tick()
    {
        if (!IsOnScreen) return;
        _levels.Watch(WatchedUniverse());   // the Control screen's fixture can change universe, or another be picked
        switch (CurrentPage)
        {
            case Page.Monitor: Monitor.Tick(ActiveUniverse); break;
            case Page.Channels: Channels.Tick(); break;
            case Page.Control: Control.Tick(); break;
        }
    }

    partial void OnSelectedPageChanged(int value)
    {
        TellWhatIsOnScreen();
        PageChanged?.Invoke();
    }

    partial void OnActiveUniverseChanged(int value)
    {
        OnPropertyChanged(nameof(ActiveUniverseChoice));
        Channels.SetUniverse(value);
    }

    void TellWhatIsOnScreen()
    {
        Outputs.SetOnScreen(IsOnScreen && CurrentPage == Page.DmxSetup);
        _levels.Watch(WatchedUniverse());
    }

    int? WatchedUniverse() => !IsOnScreen ? null : CurrentPage switch
    {
        Page.Channels or Page.Monitor => ActiveUniverse,
        Page.Control => Control.SelectedFixture?.Universe,
        _ => null,
    };

    void OnApplied()
    {
        var universes = _session.Engine.Universes;
        if (universes.SequenceEqual(UniverseNumbers)) return;
        int active = ActiveUniverse;
        UniverseNumbers.Clear();
        foreach (var universe in universes) UniverseNumbers.Add(universe);
        ActiveUniverse = universes.Contains(active) ? active : universes.FirstOrDefault();
        // Pickers bound to ActiveUniverse lost their selection when the list was cleared; re-announce it even if unchanged.
        OnPropertyChanged(nameof(ActiveUniverseChoice));
    }
}
