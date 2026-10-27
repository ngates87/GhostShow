using Avalonia.Controls;
using Avalonia.Interactivity;
using GhostShow.App.Dialogs;
using GhostShow.App.ViewModels;

namespace GhostShow.App;

public partial class MainWindow : Window
{
    bool _outputStopped;
    bool _closing;   // the save prompt is open or shutdown is under way

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel viewModel) viewModel.ExitRequested += Close;
        };
    }

    async void OnAbout(object? sender, RoutedEventArgs e) => await new AboutDialog().ShowDialog(this);

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
