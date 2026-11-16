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
    public const double HeaderHeight = 36;

    readonly PlaybackViewModel _playback;
    readonly InputMonitorViewModel? _inputMonitor;
    readonly LightingViewModel? _lighting;
    readonly object? _diagnostics;
    readonly GhostShow.Core.Storage.PanelWidths? _settings;

    public BottomWorkspaceViewModel(PlaybackViewModel playback, InputMonitorViewModel? inputMonitor,
        LightingViewModel? lighting, object? diagnostics, GhostShow.Core.Storage.PanelWidths? settings = null)
    {
        _playback = playback;
        _inputMonitor = inputMonitor;
        _lighting = lighting;
        _diagnostics = diagnostics;
        _settings = settings;

        IEnumerable<BottomPaneKind> initial = settings?.TimelinePanes is { } saved
            ? saved.Select(Parse).Where(k => k is not null).Select(k => k!.Value)
            : inputMonitor is null ? [BottomPaneKind.Playing] : [BottomPaneKind.InputMonitor, BottomPaneKind.Playing];
        foreach (var kind in initial.Distinct())
            if (Create(kind) is { } pane) Panes.Add(pane);
        if (Panes.Count == 0) Panes.Add(new PlayingPaneViewModel(playback));
        Panes.CollectionChanged += OnPanesChanged;
        SelectedPane = Panes.FirstOrDefault(p => p.Key == settings?.TimelineSelectedPane) ?? Panes[0];
        PanelHeight = settings?.TimelineBottom ?? UsualHeight;
        IsOpen = settings?.TimelineBottomOpen ?? false;
    }

    public ObservableCollection<BottomPaneViewModel> Panes { get; } = new();

    public bool HasInputMonitor => _inputMonitor is not null;
    public bool HasLighting => _lighting is not null;
    public bool HasDiagnostics => _diagnostics is not null;

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
        BottomPaneKind.Playing => new PlayingPaneViewModel(_playback),
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
    }

    static BottomPaneKind? Parse(string key) => Enum.TryParse<BottomPaneKind>(key, out var kind) ? kind : null;
}
