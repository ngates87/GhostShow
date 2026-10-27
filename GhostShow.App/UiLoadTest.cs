using Avalonia.Threading;
using GhostShow.App.ViewModels;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Timeline;
using Microsoft.Extensions.Logging;

namespace GhostShow.App;

/// <summary>
/// Developer hook for measuring UI responsiveness under load (docs/performance.md). Never active otherwise.
/// - GHOSTSHOW_UI_LOAD_TEST=1: the app opens on the DMX monitor and a worker thread changes all 512 channels of the
///   first universe ~1,000 times a second.
/// - GHOSTSHOW_UI_LOAD_TEST=timeline: the current show is replaced by a busy one (20 RGBW fixtures, a 60 s looping
///   timeline with 2,000 fades and a marker every 5 s), the app opens on the Timeline screen and plays it, and every
///   2 s a lighting point is moved through the editor (undoable edit, recompile and swap on the UI thread), as during
///   editing while playing.
/// Either way, the UI-thread wait is logged every 30 s.
/// </summary>
sealed class UiLoadTest(MainViewModel viewModel, AppServices services, ILogger logger) : IDisposable
{
    public const string EnvironmentVariable = "GHOSTSHOW_UI_LOAD_TEST";

    readonly CancellationTokenSource _stop = new();

    static string? Mode => Environment.GetEnvironmentVariable(EnvironmentVariable);

    public static bool IsRequested => Mode is "1" or "timeline";

    public void Start()
    {
        if (Mode == "timeline") StartTimeline();
        else StartChannels();
        Task.Run(ReportAsync);
    }

    void StartChannels()
    {
        var engine = services.Session.Engine;
        logger.LogWarning("UI load test running: all 512 channels of universe {Universe} are being changed continuously.", engine.Universes[0]);
        viewModel.ShowEditor();
        viewModel.CurrentPage = Page.Monitor;
        Task.Factory.StartNew(() =>
        {
            int universe = engine.Universes[0], step = 0;
            while (!_stop.IsCancellationRequested)
            {
                step++;
                for (int address = 1; address <= 512; address++) engine.SetChannel(universe, address, (address * 7 + step) & 0xFF);
                Thread.Sleep(1);
            }
        }, TaskCreationOptions.LongRunning);
    }

    void StartTimeline()
    {
        var session = services.Session;
        session.Project.Fixtures.Clear();
        for (int i = 0; i < 20; i++)
            session.Project.Fixtures.Add(new FixtureInstance { Name = $"Par {i + 1}", ProfileId = "generic/rgbw", ModeName = "5 channel (dimmer first)", StartAddress = 1 + i * 5 });
        session.Apply();

        var random = new Random(1);
        ChannelFunction[] controls = [ChannelFunction.Intensity, ChannelFunction.Red, ChannelFunction.Green, ChannelFunction.Blue, ChannelFunction.White];
        var tracks = Enumerable.Range(0, 4).Select(i => new TimelineTrack { Name = $"Lighting {i + 1}", Kind = TrackKind.Lighting }).ToList();
        for (int i = 0; i < 2000; i++)
            tracks[i % tracks.Count].Events.Add(new LevelsEvent
            {
                FixtureId = session.Patch.Fixtures[random.Next(session.Patch.Fixtures.Count)].Id,
                Start = TimeSpan.FromSeconds(random.NextDouble() * 54),
                Fade = TimeSpan.FromSeconds(random.Next(6)),
                Levels = controls.Take(random.Next(1, 6)).Select(c => new Level((byte)random.Next(256), Control: c)).ToList(),
            });
        var markers = new TimelineTrack { Name = "Cues", Kind = TrackKind.Markers };
        for (int s = 0; s < 60; s += 5) markers.Events.Add(new MarkerEvent { Name = $"M{s}", Start = TimeSpan.FromSeconds(s) });
        var timeline = new ShowTimeline { Name = "Load test", Length = TimeSpan.FromSeconds(60), Loop = true, Tracks = [.. tracks, markers] };
        LevelEventConversion.Upgrade(timeline);   // lighting as the editor stores it: blocks holding each channel's points
        session.Project.Timelines = [timeline];

        viewModel.Timeline.Reload();
        viewModel.ShowEditor();
        viewModel.Playback.PlayPauseCommand.Execute(null);
        logger.LogWarning("UI load test running: playing a 2,000-event timeline on the Timeline screen, editing it every 2 s.");

        var points = tracks.SelectMany(t => t.Events.OfType<LightingClip>()).SelectMany(c => c.Lanes).SelectMany(l => l.Points).ToList();
        var editTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Normal, (_, _) =>
        {
            viewModel.Timeline.SelectPoint(points[random.Next(points.Count)], additive: false);
            viewModel.Timeline.MoveSelectedPoints(TimeSpan.FromSeconds(random.Next(2) == 0 ? 0.5 : -0.5), random.Next(-20, 21));
        });
        editTimer.Start();
        _stop.Token.Register(() => Dispatcher.UIThread.Post(editTimer.Stop));
    }

    async Task ReportAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
        {
            var ui = services.Probe.Snapshot();
            logger.LogWarning("UI load test: UI thread wait p50 {P50:0.0} ms, p95 {P95:0.0} ms, max {Max:0.0} ms over the last {Seconds} s.",
                              ui.P50Ms, ui.P95Ms, ui.MaxMs, ui.Samples / 10);
        }
    }

    public void Dispose() => _stop.Cancel();
}
