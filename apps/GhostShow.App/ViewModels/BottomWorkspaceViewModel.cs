using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GhostShow.App.ViewModels;

public enum BottomPaneKind { InputMonitor, Playing, FixtureSliders, RawChannels, DmxMonitor, Diagnostics }

public abstract class BottomPaneViewModel(string key, string title, bool canClose)
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public bool CanClose { get; } = canClose;
}

public sealed class InputMonitorPaneViewModel(InputMonitorViewModel? monitor) : BottomPaneViewModel("InputMonitor", "Input monitor", false)
{
    public InputMonitorViewModel? Monitor { get; } = monitor;
}

public sealed class PlayingPaneViewModel(PlaybackViewModel playback) : BottomPaneViewModel("Playing", "Playing", false)
{
    public PlaybackViewModel Playback { get; } = playback;
}

public sealed class FixtureSlidersPaneViewModel(FixtureControlViewModel control) : BottomPaneViewModel("FixtureSliders", "Fixture sliders", true)
{
    public FixtureControlViewModel Control { get; } = control;
}

public sealed class RawChannelsPaneViewModel(LightingViewModel lighting) : BottomPaneViewModel("RawChannels", "Raw channels", true)
{
    public LightingViewModel Lighting { get; } = lighting;
}

public sealed class DmxMonitorPaneViewModel(LightingViewModel lighting) : BottomPaneViewModel("DmxMonitor", "DMX monitor", true)
{
    public LightingViewModel Lighting { get; } = lighting;
}

public sealed class DiagnosticsPaneViewModel(object diagnostics) : BottomPaneViewModel("Diagnostics", "Diagnostics", true)
{
    public object Diagnostics { get; } = diagnostics;
}

/// <summary>Resizable, collapsible timeline workspace with addable views that reuse the existing show screens.</summary>
public sealed partial class BottomWorkspaceViewModel : ObservableObject
{
    public const double UsualHeight = 210;

    /// <summary>The height a view of faders needs to show them whole (they shrink only so far: see ChannelsView).</summary>
    public const double FaderHeight = 320;
    /// <summary>Its height folded: only its tabs.</summary>
    public const double HeaderHeight = 32;

    readonly PlaybackViewModel _playback;
    readonly InputMonitorViewModel? _inputMonitor;
    readonly LightingViewModel? _lighting;
    readonly object? _diagnostics;
    readonly GhostShow.Core.Storage.PanelWidths? _settings;

    /// <param name="offersPlaying">
    /// Whether the Playing list is one of its views: where there's no Buttons panel (a browser). On the show computer it's
    /// the Buttons panel's Playing tab, where it has the height.
    /// </param>
    public BottomWorkspaceViewModel(PlaybackViewModel playback, InputMonitorViewModel? inputMonitor,
        LightingViewModel? lighting, object? diagnostics, GhostShow.Core.Storage.PanelWidths? settings = null, bool offersPlaying = true)
    {
        OffersPlaying = offersPlaying;
        _playback = playback;
        _inputMonitor = inputMonitor;
        _lighting = lighting;
        _diagnostics = diagnostics;
        _settings = settings;

        IEnumerable<BottomPaneKind> initial = settings?.TimelinePanes is { } saved
            ? saved.Select(Parse).Where(k => k is not null).Select(k => k!.Value)
            : inputMonitor is null ? [BottomPaneKind.Playing] : [BottomPaneKind.InputMonitor];
        foreach (var kind in initial.Distinct())
            if (Create(kind) is { } pane) Panes.Add(pane);
        Panes.CollectionChanged += OnPanesChanged;
        SelectedPane = Panes.FirstOrDefault(p => p.Key == settings?.TimelineSelectedPane) ?? Panes.FirstOrDefault();
        PanelHeight = settings?.TimelineBottom ?? UsualHeight;
        IsOpen = settings?.TimelineBottomOpen ?? false;
    }

    public ObservableCollection<BottomPaneViewModel> Panes { get; } = new();

    public bool HasInputMonitor => _inputMonitor is not null;
    public bool HasLighting => _lighting is not null;
    public bool HasDiagnostics => _diagnostics is not null;
    public bool OffersPlaying { get; }

    /// <summary>Every view closed: it says how to add one.</summary>
    public bool IsEmpty => Panes.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualHeight), nameof(ToggleLabel))]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualHeight))]
    public partial double PanelHeight { get; set; }

    public double ActualHeight => IsOpen ? PanelHeight : HeaderHeight;
    public string ToggleLabel => IsOpen ? "⌄" : "⌃";

    [ObservableProperty]
    public partial BottomPaneViewModel? SelectedPane { get; set; }

    /// <summary>A tab clicked while it's folded opens it.</summary>
    partial void OnSelectedPaneChanged(BottomPaneViewModel? oldValue, BottomPaneViewModel? newValue)
    {
        if (oldValue is not null && newValue is not null) IsOpen = true;
        // Faders shown in a short workspace would be cut off: it grows to fit them (it can still be dragged shorter).
        if (oldValue is not null && newValue is FixtureSlidersPaneViewModel or RawChannelsPaneViewModel && PanelHeight < FaderHeight) PanelHeight = FaderHeight;
    }

    [RelayCommand]
    void ToggleOpen() => IsOpen = !IsOpen;

    [RelayCommand]
    void AddPane(BottomPaneKind kind)
    {
        var existing = Panes.FirstOrDefault(p => p.Key == kind.ToString());
        if (existing is not null) SelectedPane = existing;
        else if (Create(kind) is { } pane)
        {
            Panes.Add(pane);
            SelectedPane = pane;
        }
        IsOpen = true;
    }

    [RelayCommand]
    void ClosePane(BottomPaneViewModel? pane)
    {
        if (pane is null || !pane.CanClose) return;
        int index = Panes.IndexOf(pane);
        if (index < 0) return;
        Panes.RemoveAt(index);
        if (SelectedPane == pane) SelectedPane = Panes.Count == 0 ? null : Panes[Math.Min(index, Panes.Count - 1)];
    }

    BottomPaneViewModel? Create(BottomPaneKind kind) => kind switch
    {
        BottomPaneKind.InputMonitor when _inputMonitor is not null => new InputMonitorPaneViewModel(_inputMonitor),
        BottomPaneKind.Playing when OffersPlaying => new PlayingPaneViewModel(_playback),
        BottomPaneKind.FixtureSliders when _lighting is not null => new FixtureSlidersPaneViewModel(_lighting.Control),
        BottomPaneKind.RawChannels when _lighting is not null => new RawChannelsPaneViewModel(_lighting),
        BottomPaneKind.DmxMonitor when _lighting is not null => new DmxMonitorPaneViewModel(_lighting),
        BottomPaneKind.Diagnostics when _diagnostics is not null => new DiagnosticsPaneViewModel(_diagnostics),
        _ => null,
    };

    void OnPanesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_settings is not null) _settings.TimelinePanes = Panes.Select(p => p.Key).ToList();
        OnPropertyChanged(nameof(Panes));
        OnPropertyChanged(nameof(IsEmpty));
    }

    static BottomPaneKind? Parse(string key) => Enum.TryParse<BottomPaneKind>(key, out var kind) ? kind : null;
}
