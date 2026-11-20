using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GhostShow.App.ViewModels;
using GhostShow.Core.Diagnostics;
using GhostShow.Core.Storage;
using GhostShow.Server;
using Microsoft.Extensions.Logging;

namespace GhostShow.App;

/// <summary>
/// Composition root: starts the show computer's side (<see cref="ShowServices"/>, from GhostShow.Server) and wires it
/// to the window. The window appears at once; machine settings and the profile library load asynchronously, so nothing
/// slow runs on the UI thread.
/// </summary>
public partial class App : Application
{
    readonly RecentEventsLog _recentEvents = new();
    ILoggerFactory? _loggerFactory;
    ShowServices? _services;

    /// <summary>This copy's hold on being the only GhostShow running (null in tests and previews).</summary>
    public SingleInstance? SingleInstance { get; init; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            // Another copy was started: it quits, and this window comes forward instead.
            if (SingleInstance is { } instance)
                instance.ShowRequested += () => Avalonia.Threading.Dispatcher.UIThread.Post(window.BringToFront);
            desktop.Exit += (_, _) => { _services?.Probe.Dispose(); _loggerFactory?.Dispose(); };
            StartAsync(window);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>async void: this is the app's top-level startup, so there is no caller to return a Task to.</summary>
    async void StartAsync(MainWindow window)
    {
        _loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(_recentEvents)
            .AddSimpleConsole(options => { options.SingleLine = true; options.TimestampFormat = "HH:mm:ss "; }));
        Controls.TimelineCanvas.Log = _loggerFactory.CreateLogger<Controls.TimelineCanvas>();

        // The show computer's side, hosted here on the UI thread for now (docs/plan-remote.md, step 4).
        var (services, settingsError) = await ShowServices.StartAsync(_loggerFactory, AppPaths.Default, _recentEvents, UiShowThread.Instance);
        _services = services;
        var viewModel = new MainViewModel(services, window.StorageProvider, new Dialogs.SaveChangesDialogPrompt(window),
                                          _loggerFactory.CreateLogger<MainViewModel>());
        window.DataContext = viewModel;
        await viewModel.StartAsync(settingsError);

        if (UiLoadTest.IsRequested)
        {
            var loadTest = new UiLoadTest(viewModel, services, _loggerFactory.CreateLogger<UiLoadTest>());
            window.Closing += (_, _) => loadTest.Dispose();
            loadTest.Start();
        }
    }
}
