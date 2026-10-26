using GhostShow.App.ViewModels;
using GhostShow.Core.Engine;
using Microsoft.Extensions.Logging;

namespace GhostShow.App;

/// <summary>
/// Developer hook for measuring UI responsiveness under load (docs/performance.md). Enabled by setting
/// GHOSTSHOW_UI_LOAD_TEST=1: the app opens on the DMX monitor, a worker thread changes all 512 channels of the
/// first universe ~1,000 times a second, and the UI-thread wait is logged every 30 s. Never active otherwise.
/// </summary>
sealed class UiLoadTest(MainViewModel viewModel, ShowEngine engine, UiResponsivenessProbe probe, ILogger logger) : IDisposable
{
    public const string EnvironmentVariable = "GHOSTSHOW_UI_LOAD_TEST";

    readonly CancellationTokenSource _stop = new();

    public static bool IsRequested => Environment.GetEnvironmentVariable(EnvironmentVariable) == "1";

    public void Start()
    {
        logger.LogWarning("UI load test running: all 512 channels of universe {Universe} are being changed continuously.", engine.Universes[0]);
        viewModel.SelectedPage = (int)Page.Monitor;
        Task.Factory.StartNew(ChangeChannels, TaskCreationOptions.LongRunning);
        Task.Run(ReportAsync);
    }

    void ChangeChannels()
    {
        int universe = engine.Universes[0], step = 0;
        while (!_stop.IsCancellationRequested)
        {
            step++;
            for (int address = 1; address <= 512; address++) engine.SetChannel(universe, address, (address * 7 + step) & 0xFF);
            Thread.Sleep(1);
        }
    }

    async Task ReportAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
        {
            var ui = probe.Snapshot();
            logger.LogWarning("UI load test: UI thread wait p50 {P50:0.0} ms, p95 {P95:0.0} ms, max {Max:0.0} ms over the last {Seconds} s.",
                              ui.P50Ms, ui.P95Ms, ui.MaxMs, ui.Samples / 10);
        }
    }

    public void Dispose() => _stop.Cancel();
}
