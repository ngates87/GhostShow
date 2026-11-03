using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Storage;
using GhostShow.Dmx;
using GhostShow.Dmx.Devices;
using Microsoft.Extensions.Logging;

/// <summary>
/// Sustained-output test: runs one universe on a real or simulated device for a set time while hammering the engine
/// the way a busy operator and a future timeline would, and writes a Markdown report.
///
/// Load: ~1,000 commands/s (random fixture controls on 20 patched RGBW fixtures and random raw channels), plus a show
/// save every 5 s. Measured: every frame interval (whole run, not a window), command-to-device latency (a marker on
/// channel 512 changes once a second; the time until a frame carrying it reaches the device), memory and GCs.
///
/// --dark keeps every value at 0 (the marker alternates 1 and 2), so it can run on real hardware with fixtures
/// connected without flashing them; the frame path does the same work either way.
/// </summary>
sealed class SoakTest(TimeSpan duration, IDmxOutputDevice device, bool dark, ILoggerFactory loggerFactory)
{
    const int MarkerAddress = 512;
    const int FixtureCount = 20;
    const int Universe = GhostShow.Core.Model.Project.FirstUniverse;

    readonly MeasuringDevice _device = new(device);
    readonly List<SoakSample> _samples = [];
    long _commands;
    int _saves, _saveFailures;

    public async Task<string> RunAsync()
    {
        var engine = new ShowEngine();
        var session = new ShowSession(engine, new FixtureProfileLibrary(FixtureProfileLoader.LoadBuiltIn()), loggerFactory.CreateLogger<ShowSession>());
        for (int i = 0; i < FixtureCount; i++)
            session.Project.Fixtures.Add(new FixtureInstance { Name = $"Par {i + 1}", ProfileId = "generic/rgbw", ModeName = "5 channel (dimmer first)", StartAddress = 1 + i * 5 });
        var patch = session.Apply();
        string savePath = Path.Combine(Path.GetTempPath(), $"ghostshow-soak-{Environment.ProcessId}");

        var service = new DmxOutputService(engine, Universe, _device, loggerFactory.CreateLogger<DmxOutputService>());
        service.Start();

        using var stop = new CancellationTokenSource(duration);
        var started = Stopwatch.StartNew();
        var baseline = Memory.Measure(forceCollection: true);
        var writer = Task.Factory.StartNew(() => WriteCommands(engine, patch.Fixtures, stop.Token), TaskCreationOptions.LongRunning);
        var saver = SaveRepeatedlyAsync(session, savePath, stop.Token);

        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), stop.Token); }
            catch (OperationCanceledException) { }
            var sample = new SoakSample(started.Elapsed, _device.Snapshot(), Memory.Measure(forceCollection: false), Interlocked.Read(ref _commands), service.Status);
            _samples.Add(sample);
            Console.WriteLine(sample.ToConsoleLine());
        }

        await writer;
        await saver;
        var end = Memory.Measure(forceCollection: true);
        bool stoppedCleanly = await service.StopAsync();
        if (Directory.Exists(savePath)) Directory.Delete(savePath, recursive: true);

        return Report(started.Elapsed, baseline, end, stoppedCleanly, service.LastError);
    }

    void WriteCommands(ShowEngine engine, IReadOnlyList<PatchedFixture> fixtures, CancellationToken stop)
    {
        var random = new Random(1);
        ChannelFunction[] controls = [ChannelFunction.Intensity, ChannelFunction.Red, ChannelFunction.Green, ChannelFunction.Blue, ChannelFunction.White];
        long nextMarker = 0;
        int markerValue = 0;

        while (!stop.IsCancellationRequested)
        {
            int value = dark ? 0 : random.Next(256);
            if (random.Next(2) == 0) engine.SetFixtureControl(fixtures[random.Next(fixtures.Count)].Id, controls[random.Next(controls.Length)], value);
            else engine.SetChannel(Universe, random.Next(101, MarkerAddress), value);
            Interlocked.Increment(ref _commands);

            if (Stopwatch.GetTimestamp() >= nextMarker)
            {
                markerValue = dark ? (markerValue == 1 ? 2 : 1) : (markerValue % 255) + 1;
                _device.ExpectMarker((byte)markerValue);
                engine.SetChannel(Universe, MarkerAddress, markerValue);
                nextMarker = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            }
            Thread.Sleep(1);   // ~1,000 commands/s: faster than any operator, comparable to a busy timeline
        }
    }

    async Task SaveRepeatedlyAsync(ShowSession session, string path, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await session.SaveAsync(path, CancellationToken.None);
                _saves++;
                await Task.Delay(TimeSpan.FromSeconds(5), stop);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { _saveFailures++; }
        }
    }

    string Report(TimeSpan elapsed, Memory baseline, Memory end, bool stoppedCleanly, string? lastError)
    {
        var final = _device.Snapshot();
        var report = new StringBuilder();
        report.AppendLine($"# Soak test: {_device.Info.Kind}, {elapsed.TotalMinutes:0} minutes{(dark ? " (dark)" : "")}");
        report.AppendLine();
        report.AppendLine($"- Date: {DateTime.Now:yyyy-MM-dd HH:mm}");
        report.AppendLine($"- Machine: {Environment.MachineName}, {RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture}, {Environment.ProcessorCount} logical CPUs");
        report.AppendLine($"- Runtime: {RuntimeInformation.FrameworkDescription}");
        report.AppendLine($"- Device: {_device.Info}");
        report.AppendLine($"- Load: {FixtureCount} patched RGBW fixtures; {Interlocked.Read(ref _commands) / elapsed.TotalSeconds:0} commands/s; show saved every 5 s ({_saves} saves, {_saveFailures} failed)");
        report.AppendLine();
        report.AppendLine("| Measure | Result |");
        report.AppendLine("|---|---|");
        report.AppendLine($"| Frames sent | {final.Frames:N0} ({final.Frames / elapsed.TotalSeconds:0.0} frames/s) |");
        report.AppendLine($"| Frame interval p50 / p95 / p99 / max | {final.IntervalP50Ms:0.00} / {final.IntervalP95Ms:0.00} / {final.IntervalP99Ms:0.00} / {final.IntervalMaxMs:0.0} ms |");
        report.AppendLine($"| Intervals over 50 ms | {final.IntervalsOver50Ms} |");
        report.AppendLine($"| Command → device latency p50 / p95 / max ({final.LatencySamples} samples) | {final.LatencyP50Ms:0.0} / {final.LatencyP95Ms:0.0} / {final.LatencyMaxMs:0.0} ms |");
        report.AppendLine($"| Device errors | {final.Errors}{(lastError is null ? "" : $" (last: {lastError})")} |");
        report.AppendLine($"| Managed heap after full GC, start → end | {baseline.ManagedMb:0.0} → {end.ManagedMb:0.0} MB |");
        report.AppendLine($"| Working set, start → end | {baseline.WorkingSetMb:0} → {end.WorkingSetMb:0} MB |");
        report.AppendLine($"| GCs gen0 / gen1 / gen2 | {end.Gen0 - baseline.Gen0} / {end.Gen1 - baseline.Gen1} / {end.Gen2 - baseline.Gen2} |");
        report.AppendLine($"| Stopped cleanly with blackout | {(stoppedCleanly ? "yes" : "NO")} |");
        report.AppendLine();
        report.AppendLine("Per minute (cumulative frame statistics):");
        report.AppendLine();
        report.AppendLine("| Minute | Frames/s | Interval p95 / max | Latency p95 / max | Heap | Working set | Status |");
        report.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var s in _samples) report.AppendLine(s.ToMarkdownRow());
        return report.ToString();
    }

    sealed record SoakSample(TimeSpan Elapsed, MeasuringDevice.Stats Device, Memory Memory, long Commands, DmxOutputStatus Status)
    {
        public string ToConsoleLine() =>
            $"{Elapsed.TotalMinutes,4:0} min  {Device.Frames / Elapsed.TotalSeconds,5:0.0} fps  interval p95 {Device.IntervalP95Ms:0.0} max {Device.IntervalMaxMs:0.0} ms  " +
            $"latency p95 {Device.LatencyP95Ms:0.0} ms  heap {Memory.ManagedMb:0.0} MB  ws {Memory.WorkingSetMb:0} MB  {Status}";

        public string ToMarkdownRow() =>
            $"| {Elapsed.TotalMinutes:0} | {Device.Frames / Elapsed.TotalSeconds:0.0} | {Device.IntervalP95Ms:0.0} / {Device.IntervalMaxMs:0.0} ms | " +
            $"{Device.LatencyP95Ms:0.0} / {Device.LatencyMaxMs:0.0} ms | {Memory.ManagedMb:0.0} MB | {Memory.WorkingSetMb:0} MB | {Status} |";
    }

    sealed record Memory(double ManagedMb, double WorkingSetMb, int Gen0, int Gen1, int Gen2)
    {
        public static Memory Measure(bool forceCollection)
        {
            if (forceCollection)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            using var process = Process.GetCurrentProcess();
            return new Memory(GC.GetTotalMemory(false) / 1048576.0, process.WorkingSet64 / 1048576.0,
                              GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        }
    }
}

/// <summary>
/// Wraps a device to measure, over the whole run: every interval between frames (a histogram in 0.1 ms buckets, so
/// percentiles are exact to 0.1 ms without storing every frame) and how long a marker value took to reach the device.
/// SendFrame runs on the output thread; Snapshot and ExpectMarker on others, hence the lock.
/// </summary>
sealed class MeasuringDevice(IDmxOutputDevice inner) : IDmxOutputDevice
{
    const int BucketsPerMs = 10, MaxMs = 1000;

    readonly Lock _lock = new();
    readonly long[] _intervalBuckets = new long[MaxMs * BucketsPerMs + 1];
    readonly List<double> _latenciesMs = [];
    long _lastFrame = -1, _frames, _errors, _intervalsOver50Ms;
    double _maxIntervalMs;
    int _expectedMarker = -1;
    long _markerSetAt;

    public DmxDeviceInfo Info => inner.Info;

    public Task ConnectAsync(CancellationToken cancellationToken) => inner.ConnectAsync(cancellationToken);

    public Task DisconnectAsync()
    {
        lock (_lock) _lastFrame = -1;   // don't count a reconnect gap as one long frame
        return inner.DisconnectAsync();
    }

    public void ExpectMarker(byte value)
    {
        lock (_lock)
        {
            _expectedMarker = value;
            _markerSetAt = Stopwatch.GetTimestamp();
        }
    }

    public void SendFrame(ReadOnlySpan<byte> channels)
    {
        try
        {
            inner.SendFrame(channels);
        }
        catch (DmxDeviceException)
        {
            lock (_lock) _errors++;
            throw;
        }

        long now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            _frames++;
            if (_lastFrame >= 0)
            {
                double ms = Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds;
                _intervalBuckets[Math.Min((int)(ms * BucketsPerMs), _intervalBuckets.Length - 1)]++;
                _maxIntervalMs = Math.Max(_maxIntervalMs, ms);
                if (ms > 50) _intervalsOver50Ms++;
            }
            _lastFrame = now;

            if (_expectedMarker >= 0 && channels[511] == _expectedMarker)
            {
                _latenciesMs.Add(Stopwatch.GetElapsedTime(_markerSetAt, now).TotalMilliseconds);
                _expectedMarker = -1;
            }
        }
    }

    public Stats Snapshot()
    {
        lock (_lock)
        {
            var latencies = _latenciesMs.Order().ToArray();
            double LatencyAt(double f) => latencies.Length == 0 ? 0 : latencies[Math.Min(latencies.Length - 1, (int)Math.Ceiling(f * latencies.Length) - 1)];
            return new Stats(_frames, IntervalAt(0.50), IntervalAt(0.95), IntervalAt(0.99), _maxIntervalMs, _intervalsOver50Ms,
                             latencies.Length, LatencyAt(0.50), LatencyAt(0.95), latencies.Length == 0 ? 0 : latencies[^1], _errors);
        }
    }

    double IntervalAt(double fraction)
    {
        long total = _intervalBuckets.Sum(), target = (long)Math.Ceiling(fraction * total), seen = 0;
        for (int i = 0; i < _intervalBuckets.Length; i++)
        {
            seen += _intervalBuckets[i];
            if (seen >= target && total > 0) return (i + 1) / (double)BucketsPerMs;   // upper edge of the bucket
        }
        return 0;
    }

    public void Dispose() => inner.Dispose();

    public sealed record Stats(long Frames, double IntervalP50Ms, double IntervalP95Ms, double IntervalP99Ms, double IntervalMaxMs,
                               long IntervalsOver50Ms, int LatencySamples, double LatencyP50Ms, double LatencyP95Ms, double LatencyMaxMs, long Errors);
}
