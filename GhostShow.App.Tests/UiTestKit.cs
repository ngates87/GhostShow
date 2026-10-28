using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GhostShow.App.ViewModels;
using GhostShow.Audio;
using GhostShow.Core.Diagnostics;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Playback;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;
using GhostShow.Dmx;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhostShow.App.Tests;

/// <summary>The app on Avalonia's headless platform: real windows, layout and input, nothing on screen.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>
/// UI tests run on one shared headless UI thread. Each test runs its body there with <see cref="Run"/>; the body
/// drives a real <see cref="MainWindow"/> with mouse and keyboard input (see <see cref="WindowRig"/>).
/// </summary>
static class Ui
{
    static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder)));

    public static Task Run(Action body) => Session.Value.Dispatch(body, TestContext.Current.CancellationToken);
}

/// <summary>
/// The main window on a show built in code: an RGBW fixture at 20 and a dimmer at 10, and a "Main" timeline
/// with a lighting track (Red of the RGB fixture) and an audio track, open in the editor.
/// </summary>
sealed class WindowRig : IDisposable
{
    readonly string _config = Path.Combine(Path.GetTempPath(), "ghostshow-ui-tests", Guid.NewGuid().ToString("N"));
    readonly UiResponsivenessProbe _probe = new();

    public WindowRig()
    {
        // Built-in profiles: starting the window loads the full library, which replaces whatever the session had.
        Session = new ShowSession(new ShowEngine(), new FixtureProfileLibrary(FixtureProfileLoader.LoadBuiltIn()), NullLogger<ShowSession>.Instance);
        Rgb = new FixtureInstance { Name = "Entry LED", ProfileId = "generic/rgbw", ModeName = "5 channel (dimmer first)", StartAddress = 20 };
        Session.Project.Fixtures.AddRange([new FixtureInstance { Name = "Fog", ProfileId = "generic/dimmer", ModeName = "1 channel", StartAddress = 10 }, Rgb]);
        var lighting = Session.Project.Timelines[0].Tracks[0];
        lighting.Channels.Add(new ChannelLane { Target = LaneTarget.FixtureControl(Rgb.Id, ChannelFunction.Red) });
        Session.Apply();

        Audio = new AudioService(NullLogger.Instance, useNullDevice: true);
        Manager = new PlaybackManager(Session.Engine, Audio, new StopwatchClock());
        var services = new AppServices(Session, new OutputManager(Session.Engine, NullLoggerFactory.Instance), Manager,
                                       new PlaybackLoop(Manager, NullLogger.Instance), Audio, new MachineSettings(),
                                       new AppPaths(_config), new RecentEventsLog(), _probe);
        Window = new MainWindow { Width = 1400, Height = 820 };
        ViewModel = new MainViewModel(services, Window.StorageProvider, new Dialogs.SaveChangesDialogPrompt(Window), NullLogger<MainViewModel>.Instance);
        Window.DataContext = ViewModel;
        Window.Show();
        var start = ViewModel.StartAsync(null);
        Pump(300);
        ViewModel.ShowEditor();
        Editor.PixelsPerSecond = 60;
        Pump(300);
    }

    public ShowSession Session { get; }
    public FixtureInstance Rgb { get; }
    public AudioService Audio { get; }
    public PlaybackManager Manager { get; }

    /// <summary>The open timeline's transport status.</summary>
    public PlaybackStatus Status => Manager.StatusOf(Editor.SelectedTimeline!.Id);
    public MainWindow Window { get; }
    public MainViewModel ViewModel { get; }
    public TimelineEditorViewModel Editor => ViewModel.Timeline;
    public TimelineTrack Lighting => Session.Project.Timelines[0].Tracks[0];

    /// <summary>Lets the UI thread catch up: layout, bindings, posted work.</summary>
    public static void Pump(int milliseconds = 100)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        } while (DateTime.UtcNow < until);
    }

    public T Find<T>(Func<T, bool>? where = null) where T : Visual =>
        Window.GetVisualDescendants().OfType<T>().First(c => where?.Invoke(c) ?? true);

    public Point CenterOf(Visual visual) => visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), Window)!.Value;

    public void Click(Point at, MouseButton button = MouseButton.Left, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Window.MouseDown(at, button, modifiers);
        Window.MouseUp(at, button, modifiers);
        Pump(50);
    }

    public void DoubleClick(Point at)
    {
        Click(at);
        Click(at);
        Pump(100);
    }

    public void Drag(Point from, Point to, int steps = 8)
    {
        Window.MouseDown(from, MouseButton.Left);
        Pump(20);
        for (int i = 1; i <= steps; i++)
        {
            Window.MouseMove(from + (to - from) * ((double)i / steps));
            Pump(15);
        }
        Window.MouseUp(to, MouseButton.Left);
        Pump(100);
    }

    public void Key(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Window.KeyPress(key, modifiers, PhysicalKey.None, null);
        Pump(80);
    }

    public void Type(string text)
    {
        Window.KeyTextInput(text);
        Pump(50);
    }

    // ---- the timeline canvas ----

    public Controls.TimelineCanvas Canvas => Find<Controls.TimelineCanvas>();

    /// <summary>
    /// A point in a timeline row at a time: at a level (0 = bottom, 1 = top) of an expanded lighting row's envelopes,
    /// or, without one, half way down the row.
    /// </summary>
    public Point At(TimelineRow row, double seconds, double? level = null) =>
        Canvas.TranslatePoint(new Point(seconds * Editor.PixelsPerSecond,
                                        level is { } l ? Controls.TimelineCanvas.LevelY(row, l * 255) : Controls.TimelineCanvas.RulerHeight + row.Top + row.Height / 2),
                              Window)!.Value;

    public TimelineRow TrackRow(string name) => Editor.Rows.First(r => r.Track.Name == name);

    /// <summary>Clicks a menu item with the mouse, on the popup it's shown in (a menu is its own surface).</summary>
    public void ClickMenuItem(MenuItem item)
    {
        var popup = TopLevel.GetTopLevel(item) ?? throw new InvalidOperationException($"'{item.Header}' isn't shown.");
        var at = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), popup)!.Value;
        popup.MouseDown(at, MouseButton.Left);
        popup.MouseUp(at, MouseButton.Left);
        Pump();
    }

    /// <summary>An item of the canvas's last right-click menu (or of one of its submenus), by its header.</summary>
    public MenuItem MenuItem(string header)
    {
        IEnumerable<MenuItem> All(IEnumerable<object?> items) => items.OfType<MenuItem>().SelectMany(i => All(i.Items).Prepend(i));
        return All(Canvas.LastMenu?.Items ?? throw new InvalidOperationException("No menu is open.")).First(i => i.Header as string == header);
    }

    public void Dispose()
    {
        Window.Close();
        Audio.Dispose();
        _probe.Dispose();
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }
}
