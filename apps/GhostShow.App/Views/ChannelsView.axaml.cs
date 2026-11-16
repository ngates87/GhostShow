using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GhostShow.App.ViewModels;

namespace GhostShow.App.Views;

public partial class ChannelsView : UserControl
{
    Size? _faderSize;

    /// <summary>Shown in the timeline's workspace: only the controls, without the page's heading and margins.</summary>
    public static readonly StyledProperty<bool> IsCompactProperty = AvaloniaProperty.Register<ChannelsView, bool>(nameof(IsCompact));

    public bool IsCompact { get => GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsCompactProperty) return;
        Page.Margin = IsCompact ? new Thickness(0) : new Thickness(0, 12);
        FitPage();
    }

    public ChannelsView()
    {
        InitializeComponent();
        FaderArea.SizeChanged += (_, _) => FitPage();
        FaderItems.LayoutUpdated += (_, _) =>
        {
            // The fader size is only known once one has been laid out with its styles; measure it the first time.
            if (_faderSize is null) FitPage();
        };
    }

    /// <summary>The smallest the faders get in the workspace; below that they'd be too fiddly, and they're cut off instead.</summary>
    const double SmallestScale = 0.55;

    /// <summary>
    /// Sizes the page to as many whole faders as fit the area: columns across, then as many rows as fit. In the workspace
    /// (compact), the faders first shrink to fit its height, so a short workspace still shows whole faders.
    /// </summary>
    void FitPage()
    {
        if (DataContext is not LightingViewModel viewModel) return;
        var area = FaderArea.Bounds.Size;
        _faderSize ??= MeasuredFaderSize();
        if (_faderSize is not { } measured || area.Width <= 0 || area.Height <= 0) return;

        double scale = IsCompact ? Math.Clamp(area.Height / measured.Height, SmallestScale, 1) : 1;
        FaderScale.LayoutTransform = scale < 1 ? new ScaleTransform(scale, scale) : null;
        var fader = measured * scale;
        int columns = Math.Max(1, (int)(area.Width / fader.Width));
        int rows = Math.Max(1, (int)(area.Height / fader.Height));
        viewModel.Channels.SetPageSize(columns * rows);
    }

    /// <summary>
    /// A fader's full size, including its margin, or null if none is laid out yet. Measured with room to spare: as laid
    /// out in a short area it's already cut down to that height.
    /// </summary>
    Size? MeasuredFaderSize()
    {
        if (FaderItems.ContainerFromIndex(0) is not { Bounds: { Width: > 0, Height: > 0 } } fader) return null;
        fader.Measure(Size.Infinity);
        return fader.DesiredSize;
    }
}
