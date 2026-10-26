using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GhostShow.App.ViewModels;
using GhostShow.Core.Diagnostics;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Storage;
using GhostShow.Dmx;
using Microsoft.Extensions.Logging;

namespace GhostShow.App;

/// <summary>
/// Composition root: builds the engine, session and outputs, and wires them to the window. The window appears at
/// once; machine settings and the profile library load asynchronously, so nothing slow runs on the UI thread.
/// </summary>
public partial class App : Application
{
    readonly RecentEventsLog _recentEvents = new();
    ILoggerFactory? _loggerFactory;
    UiResponsivenessProbe? _probe;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => { _probe?.Dispose(); _loggerFactory?.Dispose(); };
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
        _probe = new UiResponsivenessProbe();

        var paths = AppPaths.Default;
        var settings = await MachineSettingsStore.LoadAsync(paths);

        // Built-in profiles only for now (instant); the full library, with QLC+'s ~1,400, loads in the background.
        var builtInProfiles = new FixtureProfileLibrary(FixtureProfileLoader.LoadBuiltIn());
        var engine = new ShowEngine();
        var session = new ShowSession(engine, builtInProfiles, _loggerFactory.CreateLogger<ShowSession>());
        var outputs = new OutputManager(engine, _loggerFactory);

        var viewModel = new MainViewModel(session, outputs, settings.Settings, paths, window.StorageProvider,
                                          _recentEvents, _probe, _loggerFactory.CreateLogger<MainViewModel>());
        window.DataContext = viewModel;
        await viewModel.StartAsync(settings.Error);

        if (UiLoadTest.IsRequested)
        {
            var loadTest = new UiLoadTest(viewModel, engine, _probe, _loggerFactory.CreateLogger<UiLoadTest>());
            window.Closing += (_, _) => loadTest.Dispose();
            loadTest.Start();
        }
    }
}
