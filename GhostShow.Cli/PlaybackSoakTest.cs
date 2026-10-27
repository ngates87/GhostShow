using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GhostShow.Audio;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Playback;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;
using GhostShow.Dmx;
using GhostShow.Dmx.Devices;
using Microsoft.Extensions.Logging;

/// <summary>
/// Sustained playback test: plays a busy looping timeline for a set time, editing it while it plays, and writes a
/// Markdown report.
///
/// Show: 20 RGBW fixtures, a 60 s looping timeline with 2,000 lighting events (fades of 0-5 s), a marker every 5 s,
/// and 4 sounds per loop (generated WAVs at 44.1 kHz mono and 48 kHz stereo, played on the audio null device, so
/// decoding and mixing run for real but nothing is heard). Every 2 s one lighting point is moved in time and level, then the
/// timeline is recompiled and swapped in, as editing during playback does.
///
/// Measured over the whole run: playback update intervals (the recorder's 2.5 s window, read every second, so every
/// interval is seen), the longest single update, show-clock drift against wall time, markers and audio starts
/// against the number expected, DMX frame timing, errors, and memory.
/// </summary>
sealed class PlaybackSoakTest(TimeSpan duration, ILoggerFactory loggerFactory)
{
    const int FixtureCount = 20, EventCount = 2000, Universe = GhostShow.Core.Model.Project.FirstUniverse;
    static readonly TimeSpan Length = TimeSpan.FromSeconds(60), MarkerSpacing = TimeSpan.FromSeconds(5);
    static readonly double[] SoundStarts = [3, 18.5, 31, 47];

    long _markers;
    int _edits;
    double _worstIntervalMs, _worstP95Ms, _longestTickMs, _worstDriftMs;
    readonly List<string> _minutes = [];

    public async Task<string> RunAsync()
    {
        string folder = Directory.CreateTempSubdirectory("ghostshow-playback-soak").FullName;
        try
        {
            return await RunInAsync(folder);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    async Task<string> RunInAsync(string folder)
    {
        var engine = new ShowEngine();
        var session = new ShowSession(engine, new FixtureProfileLibrary(FixtureProfileLoader.LoadBuiltIn()), loggerFactory.CreateLogger<ShowSession>());
        for (int i = 0; i < FixtureCount; i++)
            session.Project.Fixtures.Add(new FixtureInstance { Name = $"Par {i + 1}", ProfileId = "generic/rgbw", ModeName = "5 channel (dimmer first)", StartAddress = 1 + i * 5 });
        session.Apply();
        var timeline = CreateTimeline(session, folder);

        using var audioService = new AudioService(loggerFactory.CreateLogger<AudioService>(), useNullDevice: true);
        if (!await audioService.OpenAsync(null)) throw new InvalidOperationException("The audio null device didn't open.");
        var audio = new CountingAudio(audioService);
        foreach (var file in Directory.GetFiles(folder, "*.wav")) audioService.MeasureNow(file);

        var device = new MeasuringDevice(new SimulatorDevice());
        var output = new DmxOutputService(engine, Universe, device, loggerFactory.CreateLogger<DmxOutputService>());
        output.Start();

        var player = new ShowPlayer(engine, audio, new StopwatchClock());
        player.MarkerReached += _ => Interlocked.Increment(ref _markers);
        var loop = new PlaybackLoop(player, loggerFactory.CreateLogger<PlaybackLoop>());
        var compiled = session.CompileTimeline(timeline, audioService.GetLength);
        if (compiled.Issues.Count > 0) throw new InvalidOperationException($"Soak timeline is invalid: {compiled.Issues[0]}");
        player.Load(compiled);

        var baseline = Memory.Measure(forceCollection: true);
        loop.Start();
        player.Play();
        var playing = Stopwatch.StartNew();
        var random = new Random(3);
        var lighting = timeline.Tracks.SelectMany(t => t.Events.OfType<LightingClip>())
            .SelectMany(c => c.Lanes.Where(l => l.Points.Count > 0).Select(l => (Clip: c, Lane: l))).ToList();

        int seconds = 0;
        while (playing.Elapsed < duration)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            seconds++;
            Sample(player, loop, playing);

            if (seconds % 2 == 0)
            {
                // A live edit: move one point in time and level, recompile, swap in.
                var (clip, lane) = lighting[random.Next(lighting.Count)];
                var p = lane.Points[random.Next(lane.Points.Count)];
                p.Time = TimeSpan.FromTicks((long)(random.NextDouble() * clip.Length.Ticks));
                p.Value = (byte)random.Next(256);
                lane.Sort();
                player.Load(session.CompileTimeline(timeline, audioService.GetLength));
                _edits++;
            }
            if (seconds % 60 == 0) MinuteSample(seconds / 60, device, audio, playing);
        }

        player.Stop();
        await Task.Delay(PlaybackLoop.Interval * 5);
        bool loopStopped = loop.Stop(TimeSpan.FromSeconds(2));
        var end = Memory.Measure(forceCollection: true);
        bool outputStopped = await output.StopAsync();
        return Report(playing.Elapsed, device, audio, loop, baseline, end, loopStopped && outputStopped);
    }

    void Sample(ShowPlayer player, PlaybackLoop loop, Stopwatch playing)
    {
        var t = loop.Timing;
        _worstIntervalMs = Math.Max(_worstIntervalMs, t.IntervalMaxMs);
        _worstP95Ms = Math.Max(_worstP95Ms, t.IntervalP95Ms);
        _longestTickMs = Math.Max(_longestTickMs, loop.TakeLongestTick().TotalMilliseconds);
        // Edits swap the timeline without moving the playhead, so the show clock should equal wall time (mod the loop).
        var expected = TimeSpan.FromTicks(playing.Elapsed.Ticks % Length.Ticks);
        var position = player.Status.Position;
        double drift = Math.Abs((position - expected).TotalMilliseconds);
        drift = Math.Min(drift, Length.TotalMilliseconds - drift);   // either side of a loop wrap
        _worstDriftMs = Math.Max(_worstDriftMs, drift);
    }

    void MinuteSample(int minute, MeasuringDevice device, CountingAudio audio, Stopwatch playing)
    {
        var d = device.Snapshot();
        var memory = Memory.Measure(forceCollection: false);
        string row = $"| {minute} | {Interlocked.Read(ref _markers)} / {ExpectedMarkers(playing.Elapsed)} | {audio.Starts} / {ExpectedSoundStarts(playing.Elapsed)} | " +
                     $"{_worstP95Ms:0.0} / {_worstIntervalMs:0.0} ms | {_longestTickMs:0.00} ms | {_worstDriftMs:0.00} ms | " +
                     $"{d.Frames / playing.Elapsed.TotalSeconds:0.0} | {memory.ManagedMb:0.0} MB | {memory.WorkingSetMb:0} MB |";
        _minutes.Add(row);
        Console.WriteLine($"{minute,4} min  markers {Interlocked.Read(ref _markers)}/{ExpectedMarkers(playing.Elapsed)}  sounds {audio.Starts}/{ExpectedSoundStarts(playing.Elapsed)}  " +
                          $"update p95 {_worstP95Ms:0.0} max {_worstIntervalMs:0.0} ms  longest {_longestTickMs:0.00} ms  drift {_worstDriftMs:0.00} ms  " +
                          $"heap {memory.ManagedMb:0.0} MB  ws {memory.WorkingSetMb:0} MB");
    }

    /// <summary>Markers at 5, 10, ... 55 s and at 0 s, which is crossed when the loop wraps (not at the very start).</summary>
    static long ExpectedMarkers(TimeSpan elapsed)
    {
        long perLoop = (long)(Length / MarkerSpacing);
        long loops = elapsed.Ticks / Length.Ticks;
        var into = TimeSpan.FromTicks(elapsed.Ticks % Length.Ticks);
        return loops * perLoop + (long)(into / MarkerSpacing);
    }

    static long ExpectedSoundStarts(TimeSpan elapsed)
    {
        long loops = elapsed.Ticks / Length.Ticks;
        double into = TimeSpan.FromTicks(elapsed.Ticks % Length.Ticks).TotalSeconds;
        return loops * SoundStarts.Length + SoundStarts.Count(s => s <= into);
    }

    static ShowTimeline CreateTimeline(ShowSession session, string folder)
    {
        WriteWav(Path.Combine(folder, "mono-44k.wav"), 44100, channels: 1, TimeSpan.FromSeconds(4));
        WriteWav(Path.Combine(folder, "stereo-48k.wav"), 48000, channels: 2, TimeSpan.FromSeconds(6));
        var random = new Random(1);
        ChannelFunction[] controls = [ChannelFunction.Intensity, ChannelFunction.Red, ChannelFunction.Green, ChannelFunction.Blue, ChannelFunction.White];
        var fixtures = session.Patch.Fixtures;
        var tracks = Enumerable.Range(0, 4).Select(i => new TimelineTrack { Name = $"Lighting {i + 1}", Kind = TrackKind.Lighting }).ToList();
        for (int i = 0; i < EventCount; i++)
        {
            tracks[i % tracks.Count].Events.Add(new LevelsEvent
            {
                FixtureId = fixtures[random.Next(fixtures.Count)].Id,
                Start = TimeSpan.FromSeconds(random.NextDouble() * (Length.TotalSeconds - 6)),
                Fade = TimeSpan.FromSeconds(random.Next(6)),
                Levels = controls.Take(random.Next(1, 6)).Select(c => new Level((byte)random.Next(256), Control: c)).ToList(),
            });
        }
        var sounds = new TimelineTrack { Name = "Sound", Kind = TrackKind.Audio };
        for (int i = 0; i < SoundStarts.Length; i++)
            sounds.Events.Add(new AudioEvent
            {
                File = Path.Combine(folder, i % 2 == 0 ? "mono-44k.wav" : "stereo-48k.wav"),
                Start = TimeSpan.FromSeconds(SoundStarts[i]), FadeIn = TimeSpan.FromSeconds(0.5), FadeOut = TimeSpan.FromSeconds(1),
            });
        var markers = new TimelineTrack { Name = "Cues", Kind = TrackKind.Markers };
        for (var t = TimeSpan.Zero; t < Length; t += MarkerSpacing) markers.Events.Add(new MarkerEvent { Name = $"M{t.TotalSeconds:0}", Start = t });

        var timeline = new ShowTimeline { Name = "Soak", Length = Length, Loop = true, Tracks = [.. tracks, sounds, markers] };
        LevelEventConversion.Upgrade(timeline);   // as the editor stores lighting: blocks holding each channel's points
        session.Project.Timelines = [timeline];
        return timeline;
    }

    /// <summary>A quiet 220 Hz tone (16-bit PCM).</summary>
    static void WriteWav(string path, int rate, int channels, TimeSpan length)
    {
        int frames = (int)(rate * length.TotalSeconds), bytes = frames * channels * 2;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate); w.Write(rate * channels * 2);
        w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        for (int i = 0; i < frames; i++)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * 220 * i / rate) * 0.1 * short.MaxValue);
            for (int c = 0; c < channels; c++) w.Write(sample);
        }
    }

    string Report(TimeSpan elapsed, MeasuringDevice device, CountingAudio audio, PlaybackLoop loop, Memory baseline, Memory end, bool stoppedCleanly)
    {
        var d = device.Snapshot();
        var report = new StringBuilder();
        report.AppendLine($"# Playback soak test: {elapsed.TotalMinutes:0} minutes");
        report.AppendLine();
        report.AppendLine($"- Date: {DateTime.Now:yyyy-MM-dd HH:mm}");
        report.AppendLine($"- Machine: {Environment.MachineName}, {RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture}, {Environment.ProcessorCount} logical CPUs");
        report.AppendLine($"- Runtime: {RuntimeInformation.FrameworkDescription}");
        report.AppendLine($"- Show: {FixtureCount} RGBW fixtures; {Length.TotalSeconds:0} s looping timeline with {EventCount:N0} lighting events, " +
                          $"{SoundStarts.Length} sounds and {Length / MarkerSpacing:0} markers per loop; audio on the null device; DMX to the simulator");
        report.AppendLine($"- Live edits: {_edits} (one lighting point moved + recompile + swap every 2 s)");
        report.AppendLine();
        report.AppendLine("| Measure | Result |");
        report.AppendLine("|---|---|");
        report.AppendLine($"| Playback update interval, worst window p95 / max | {_worstP95Ms:0.0} / {_worstIntervalMs:0.0} ms (target 10 ms) |");
        report.AppendLine($"| Longest single update (evaluate + audio + markers) | {_longestTickMs:0.00} ms |");
        report.AppendLine($"| Show clock vs wall time, worst difference | {_worstDriftMs:0.00} ms |");
        report.AppendLine($"| Markers reported / expected | {Interlocked.Read(ref _markers):N0} / {ExpectedMarkers(elapsed):N0} |");
        report.AppendLine($"| Sounds started / expected | {audio.Starts:N0} / {ExpectedSoundStarts(elapsed):N0} (failed: {audio.Failures}) |");
        report.AppendLine($"| Playback update errors | {loop.ErrorCount}{(loop.LastError is { } e ? $" (last: {e})" : "")} |");
        report.AppendLine($"| DMX frames | {d.Frames:N0} ({d.Frames / elapsed.TotalSeconds:0.0}/s), interval p95 {d.IntervalP95Ms:0.0} / max {d.IntervalMaxMs:0.0} ms, errors {d.Errors} |");
        report.AppendLine($"| Managed heap after full GC, start → end | {baseline.ManagedMb:0.0} → {end.ManagedMb:0.0} MB |");
        report.AppendLine($"| Working set, start → end | {baseline.WorkingSetMb:0} → {end.WorkingSetMb:0} MB |");
        report.AppendLine($"| GCs gen0 / gen1 / gen2 | {end.Gen0 - baseline.Gen0} / {end.Gen1 - baseline.Gen1} / {end.Gen2 - baseline.Gen2} |");
        report.AppendLine($"| Stopped cleanly | {(stoppedCleanly ? "yes" : "NO")} |");
        report.AppendLine();
        report.AppendLine("Per minute (cumulative):");
        report.AppendLine();
        report.AppendLine("| Minute | Markers / expected | Sounds / expected | Update p95 / max | Longest update | Clock drift | DMX frames/s | Heap | Working set |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var row in _minutes) report.AppendLine(row);
        return report.ToString();
    }

    /// <summary>Counts what the player asks of the audio output; everything is passed through.</summary>
    sealed class CountingAudio(AudioService inner) : IAudioOutput
    {
        long _starts;
        public long Starts => Interlocked.Read(ref _starts);
        public long Failures { get; private set; }
        public int Generation => inner.Generation;

        public void Start(AudioCue cue, TimeSpan fileOffset, double volume)
        {
            Interlocked.Increment(ref _starts);
            inner.Start(cue, fileOffset, volume);
            if (inner.GetState(cue.EventId) == AudioInstanceState.Failed) Failures++;
        }

        public void Seek(Guid eventId, TimeSpan fileOffset) => inner.Seek(eventId, fileOffset);
        public void Pause(Guid eventId) => inner.Pause(eventId);
        public void Resume(Guid eventId) => inner.Resume(eventId);
        public void SetVolume(Guid eventId, double volume) => inner.SetVolume(eventId, volume);
        public void Stop(Guid eventId) => inner.Stop(eventId);
        public void StopAll() => inner.StopAll();
        public AudioInstanceState GetState(Guid eventId) => inner.GetState(eventId);
        public TimeSpan? GetLength(string path) => inner.GetLength(path);
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
