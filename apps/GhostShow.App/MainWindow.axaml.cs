using Avalonia;
using Avalonia.Controls;
using GhostShow.App.ViewModels;

namespace GhostShow.App;

public partial class MainWindow : Window
{
    bool _outputStopped;
    bool _closing;   // the save prompt is open or shutdown is under way

    /// <summary>The window: what's inside is the main view (Views/MainView), shared with the browser page.</summary>
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        Deactivated += (_, _) => View.HostLostFocus();
        Activated += (_, _) => View.HostGotFocus();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel viewModel) viewModel.ExitRequested += Close;
        };
    }

    /// <summary>The window's state before it was minimized, so bringing it back keeps it maximized if it was.</summary>
    WindowState _stateBeforeMinimized = WindowState.Normal;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && change.GetOldValue<WindowState>() is var old && old != WindowState.Minimized)
            _stateBeforeMinimized = old;
    }

    /// <summary>Shows the window in front of the others (another copy of GhostShow was started).</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = _stateBeforeMinimized;
        Show();
        Activate();
    }

    /// <summary>
    /// Closing first offers to save unsaved changes (Cancel keeps the window open), then is held until every output
    /// has sent its blackout and released its device, so fixtures don't keep the last look (or a fog machine keep
    /// fogging) after the app is gone.
    /// </summary>
    async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_outputStopped || DataContext is not MainViewModel viewModel) return;
        e.Cancel = true;   // decided asynchronously below; Close() is called again once it's safe
        if (_closing) return;   // already asking, or already shutting down
        _closing = true;
        if (!await viewModel.ConfirmCloseAsync())
        {
            _closing = false;
            return;
        }
        IsEnabled = false;
        await viewModel.ShutdownAsync();
        _outputStopped = true;
        Close();
    }
}
