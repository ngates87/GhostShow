using Avalonia;
using Avalonia.Controls;
using GhostShow.App.ViewModels;

namespace GhostShow.App.Views;

public partial class ChannelsView : UserControl
{
    Size? _faderSize;

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

    /// <summary>Sizes the page to as many whole faders as fit the area: columns across, then as many rows as fit.</summary>
    void FitPage()
    {
        if (DataContext is not MainViewModel viewModel) return;
        var area = FaderArea.Bounds.Size;
        _faderSize ??= MeasuredFaderSize();
        if (_faderSize is not { } fader || area.Width <= 0 || area.Height <= 0) return;

        int columns = Math.Max(1, (int)(area.Width / fader.Width));
        int rows = Math.Max(1, (int)(area.Height / fader.Height));
        viewModel.Channels.SetPageSize(columns * rows);
    }

    /// <summary>The size of a laid-out fader, including its margin, or null if none is laid out yet.</summary>
    Size? MeasuredFaderSize() =>
        FaderItems.ContainerFromIndex(0) is { Bounds: { Width: > 0, Height: > 0 } bounds } ? bounds.Size : null;
}
