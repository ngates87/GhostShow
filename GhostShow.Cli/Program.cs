using GhostShow.Audio;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Model;
using GhostShow.Core.Playback;
using GhostShow.Core.Timeline;
using GhostShow.Core.Storage;
using GhostShow.Dmx;
using GhostShow.Dmx.Discovery;
using Microsoft.Extensions.Logging;

return args.Length == 0 ? Usage() : args[0] switch
{
    "demo" when args.Length >= 2 => await Demo(args[1]),
    "run" when args.Length >= 3 => await Run(args),
    "ports" => Ports(),
    "detect" => await Detect(),
    "soak" => await Soak(args),
    "soak-playback" => await SoakPlayback(args),
    "library" => await Library(args),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine($"""
        ghostshow demo   <folder>                write a sample show folder
        ghostshow ports                          list serial ports
        ghostshow detect                         find USB DMX adapters (Pro and Open DMX) and their serial numbers
        ghostshow run    <show> <timeline> [output] [--silent]
                                                 <show>: a show folder (or an older single-file show);
                                                 <timeline>: its path in the tree, e.g. "Rooms/Lightning Room"
                                                 play one of the show's timelines (Ctrl+C stops)
        ghostshow library [download]             show the fixture library; download/update the Open Fixture Library
        ghostshow soak   [--minutes N] [--opendmx PORT | --usb PORT] [--dark] [--report FILE]
                                                 sustained-output test (default 30 min on the simulator);
                                                 --dark keeps fixtures at 0 so it's safe on a live rig
        ghostshow soak-playback [--minutes N] [--report FILE]
                                                 sustained playback test: a busy looping timeline, edited while it
                                                 plays (default 30 min; audio on the null device, DMX to the simulator)

        Output: by default each universe goes to the device assigned on this machine (set in the app; stored in
        {AppPaths.Default.MachineSettingsFile}), or to the simulator. To override for one run:
          --opendmx PORT   Open DMX (bare FTDI) adapter, e.g. /dev/ttyUSB0, COM3
          --usb PORT       Enttec Pro-compatible adapter
          --ip A.B.C.D     Art-Net node (use 255.255.255.255 to broadcast)
          --dry            simulator, printing channel values to the console
          --universe N     which universe the override applies to (default 1)
          --silent         don't play audio; print what would play (audio device and volume: machine settings)
        """);
    return 1;
}

static async Task<int> Demo(string path)
{
    var strobe = new FixtureInstance { Name = "Strobe", ProfileId = "generic/dimmer", ModeName = "1 channel", StartAddress = 1 };
    var glow = new FixtureInstance { Name = "Red glow", ProfileId = "generic/dimmer", ModeName = "1 channel", StartAddress = 2 };
    LevelsEvent Level(FixtureInstance fixture, double start, byte to, double fade = 0) => new()
    {
        FixtureId = fixture.Id, Start = TimeSpan.FromSeconds(start), Fade = TimeSpan.FromSeconds(fade),
        Levels = [new Level(to, Control: ChannelFunction.Intensity)],
    };

    var timeline = new ShowTimeline
    {
        Name = "Lightning Room",
        Folder = "Rooms",
        Length = TimeSpan.FromSeconds(8),
        Loop = true,
        Tracks =
        [
            new TimelineTrack
            {
                Name = "Lightning", Kind = TrackKind.Lighting,
                Events = [Level(strobe, 2.0, 255), Level(strobe, 2.08, 0), Level(strobe, 2.2, 255), Level(strobe, 2.26, 0)],
            },
            new TimelineTrack
            {
                Name = "Glow", Kind = TrackKind.Lighting,
                Events = [Level(glow, 0, 120, fade: 4), Level(glow, 4, 20, fade: 4)],
            },
            new TimelineTrack
            {
                Name = "Sound", Kind = TrackKind.Audio,
                Events = [new AudioEvent { Name = "Thunder", File = "audio/thunder.wav", Start = TimeSpan.FromSeconds(1.95) }],
            },
            new TimelineTrack { Name = "Cues", Kind = TrackKind.Markers, Events = [new MarkerEvent { Name = "Lightning", Start = TimeSpan.FromSeconds(2) }] },
        ],
    };

    LevelEventConversion.Upgrade(timeline);   // written as cues above for readability; stored as a lighting block per track
    var project = new Project { Name = "Demo Haunt", Fixtures = [strobe, glow], Timelines = [timeline] };
    project.AddFolder(timeline.Folder);
    await ShowFolderStore.SaveAsync(project, path, previousFolder: null);
    Console.WriteLine($"Wrote the show folder {path}. Put a thunder.wav in {Path.Combine(path, "audio")}, then:");
    Console.WriteLine($"  ghostshow run \"{path}\" \"Rooms/Lightning Room\" --dry");
    return 0;
}

static async Task<int> Run(string[] args)
{
    using var loggerFactory = CreateLoggerFactory();
    var log = loggerFactory.CreateLogger("ghostshow");
    string path = args[1];

    var settingsResult = await MachineSettingsStore.LoadAsync(AppPaths.Default);
    if (settingsResult.Error is not null) log.LogWarning("{Error}", settingsResult.Error);
    var settings = settingsResult.Settings;

    var engine = new ShowEngine();
    var session = new ShowSession(engine, FixtureProfileLoader.Load(AppPaths.Default, settings), loggerFactory.CreateLogger<ShowSession>());
    var opened = await session.OpenAsync(path);
    if (opened.Project is null)
    {
        Console.Error.WriteLine(opened.Error);
        return 2;
    }
    await AdoptMigratedOutputs(opened, settings, log);
    foreach (var issue in session.Patch.Issues) log.LogWarning("Patch: {Issue}", issue.Message);

    var timeline = session.Project.FindTimeline(args[2]);
    if (timeline is null)
    {
        Console.Error.WriteLine($"No timeline '{args[2]}'. Available: {string.Join(", ", session.Project.Timelines.Select(t => t.TreePath).Order())}");
        return 2;
    }

    var overrideError = ApplyOutputOverride(args, settings);
    if (overrideError is not null)
    {
        Console.Error.WriteLine(overrideError);
        return 2;
    }

    await using var outputs = new OutputManager(engine, loggerFactory);
    await outputs.ApplyAsync(engine.Universes, settings);
    using var printer = args.Contains("--dry") ? new ConsoleFramePrinter(outputs) : null;

    using var audioService = args.Contains("--silent") ? null : new AudioService(loggerFactory.CreateLogger<AudioService>());
    IAudioOutput audio = audioService ?? (IAudioOutput)new ConsoleAudioOutput();
    if (audioService is not null)
    {
        await audioService.OpenAsync(settings.Audio.DeviceName);
        audioService.MasterVolume = settings.Audio.MasterVolume;
        MeasureAudioFiles(session, timeline, audioService);
    }
    var player = new ShowPlayer(engine, audio, new StopwatchClock());
    var compiled = session.CompileTimeline(timeline, audio.GetLength);
    foreach (var issue in compiled.Issues) log.LogWarning("Timeline: {Issue}", issue.Message);
    player.Load(compiled);
    player.MarkerReached += marker => log.LogInformation("Marker: {Marker}", marker.Name);

    var loop = new PlaybackLoop(player, loggerFactory.CreateLogger<PlaybackLoop>());
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
    Console.WriteLine($"Playing '{timeline.Name}' ({(compiled.Loop ? "looping" : $"{TimeSpan.FromTicks(compiled.Length):mm\\:ss\\.f}")}). Ctrl+C to stop.");
    loop.Start();
    player.Play();
    while (!cancel.IsCancellationRequested && !player.Status.AtEnd)
    {
        try { await Task.Delay(100, cancel.Token); }
        catch (OperationCanceledException) { }
    }

    player.Stop();
    await Task.Delay(PlaybackLoop.Interval * 3);   // let the loop release the timeline and stop audio
    loop.Stop(TimeSpan.FromSeconds(2));
    bool stoppedCleanly = await outputs.StopAllAsync();
    Console.WriteLine(stoppedCleanly ? "Stopped (blackout sent)." : "Stopped, but an output didn't shut down in time; see the log.");
    return stoppedCleanly ? 0 : 3;
}

/// <summary>Measures every audio file the timeline uses before playing, so cues without a length end exactly.</summary>
static void MeasureAudioFiles(ShowSession session, ShowTimeline timeline, AudioService audio)
{
    var files = timeline.Tracks.SelectMany(t => t.Events).OfType<AudioEvent>()
        .Select(e => AudioPaths.Resolve(e.File, session.ShowFolder)).Where(File.Exists).Distinct();
    Parallel.ForEach(files, file => audio.MeasureNow(file));
}

/// <summary>Version 1 shows kept adapter assignments in the show; they now belong to this machine's settings.</summary>
static async Task AdoptMigratedOutputs(ProjectLoadResult opened, MachineSettings settings, ILogger log)
{
    if (settings.AdoptMigratedOutputs(opened.MigratedOutputs) == 0) return;
    await MachineSettingsStore.SaveAsync(settings, AppPaths.Default);
    log.LogInformation("Moved the show's output assignments to machine settings ({Path}).", AppPaths.Default.MachineSettingsFile);
}

/// <summary>Applies --opendmx/--usb/--ip/--dry to the in-memory settings only; nothing is saved.</summary>
static string? ApplyOutputOverride(string[] args, MachineSettings settings)
{
    string? universeArg = ArgValue(args, "--universe");
    int universe = Project.FirstUniverse;
    if (universeArg is not null && !int.TryParse(universeArg, out universe)) return $"--universe needs a number, not '{universeArg}'.";

    OutputDeviceConfig? device = null;
    if (args.Contains("--dry")) device = new SimulatorDeviceConfig();
    else if (ArgValue(args, "--opendmx") is { } openDmxPort) device = new OpenDmxDeviceConfig(openDmxPort, null);
    else if (ArgValue(args, "--usb") is { } proPort) device = new UsbDmxProDeviceConfig(proPort, null);
    // Art-Net addresses universes from 0 on the wire, so GhostShow's universe 1 is Art-Net universe 0.
    else if (ArgValue(args, "--ip") is { } ip) device = new ArtNetDeviceConfig(ip == "255.255.255.255" ? null : ip, universe - 1);

    if (device is not null) settings.SetOutput(universe, device);
    return null;
}

static string? ArgValue(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static async Task<int> Soak(string[] args)
{
    string? minutesArg = ArgValue(args, "--minutes");
    double minutes = 30;
    if (minutesArg is not null && !double.TryParse(minutesArg, System.Globalization.CultureInfo.InvariantCulture, out minutes))
    {
        Console.Error.WriteLine($"--minutes needs a number, not '{minutesArg}'.");
        return 2;
    }

    IDmxOutputDevice device = ArgValue(args, "--opendmx") is { } openDmxPort ? new GhostShow.Dmx.Devices.OpenDmxDevice(openDmxPort, null)
                            : ArgValue(args, "--usb") is { } proPort ? new GhostShow.Dmx.Devices.UsbProDevice(proPort, null)
                            : new GhostShow.Dmx.Devices.SimulatorDevice();
    string reportPath = ArgValue(args, "--report") ?? $"soak-{device.Info.Kind.Replace(' ', '-').ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmm}.md";

    using var loggerFactory = CreateLoggerFactory();
    Console.WriteLine($"Soak test: {device.Info}, {minutes} minutes{(args.Contains("--dark") ? ", dark" : "")}. Progress every minute.");
    var report = await new SoakTest(TimeSpan.FromMinutes(minutes), device, args.Contains("--dark"), loggerFactory).RunAsync();
    await File.WriteAllTextAsync(reportPath, report);
    Console.WriteLine();
    Console.WriteLine(report);
    Console.WriteLine($"Report written to {reportPath}.");
    return 0;
}

static async Task<int> SoakPlayback(string[] args)
{
    string? minutesArg = ArgValue(args, "--minutes");
    double minutes = 30;
    if (minutesArg is not null && !double.TryParse(minutesArg, System.Globalization.CultureInfo.InvariantCulture, out minutes))
    {
        Console.Error.WriteLine($"--minutes needs a number, not '{minutesArg}'.");
        return 2;
    }
    string reportPath = ArgValue(args, "--report") ?? $"soak-playback-{DateTime.Now:yyyyMMdd-HHmm}.md";

    using var loggerFactory = CreateLoggerFactory();
    Console.WriteLine($"Playback soak test: {minutes} minutes. Progress every minute.");
    var report = await new PlaybackSoakTest(TimeSpan.FromMinutes(minutes), loggerFactory).RunAsync();
    await File.WriteAllTextAsync(reportPath, report);
    Console.WriteLine();
    Console.WriteLine(report);
    Console.WriteLine($"Report written to {reportPath}.");
    return 0;
}

static async Task<int> Library(string[] args)
{
    var paths = AppPaths.Default;
    if (args.Length >= 2 && args[1] == "download")
    {
        Console.WriteLine($"Downloading the {OnlineFixtureLibrary.SourceName} from {OnlineFixtureLibrary.DownloadUrl}…");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GhostShow");
        try
        {
            var info = await OnlineFixtureLibrary.DownloadAsync(http, paths);
            Console.WriteLine($"Installed {info.FixtureCount} fixtures in {OnlineFixtureLibrary.Directory(paths)}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or TaskCanceledException)
        {
            Console.Error.WriteLine($"Download failed: {ex.Message} The previous library (if any) is unchanged.");
            return 2;
        }
    }

    var settings = (await MachineSettingsStore.LoadAsync(paths)).Settings;
    var library = FixtureProfileLoader.Load(paths, settings);
    var downloaded = OnlineFixtureLibrary.Installed(paths);
    Console.WriteLine($"{library.Profiles.Count} fixture profiles; {library.LoadErrors.Count} files couldn't be read.");
    Console.WriteLine(downloaded is null
        ? $"{OnlineFixtureLibrary.SourceName}: not downloaded (ghostshow library download)."
        : $"{downloaded.Source}: {downloaded.FixtureCount} fixtures, downloaded {downloaded.DownloadedAt:yyyy-MM-dd}.");
    Console.WriteLine($"QLC+ library: {(AppPaths.QlcPlusFixtureDirectories.Any(Directory.Exists) ? "found" : "not installed")}.");
    foreach (var group in library.Profiles.GroupBy(p => p.EffectiveCategory).OrderBy(g => g.Key))
        Console.WriteLine($"  {group.Key.Label(),-24} {group.Count(),5}");
    return 0;
}

static int Ports()
{
    var ports = System.IO.Ports.SerialPort.GetPortNames();
    Console.WriteLine(ports.Length == 0 ? "No serial ports found." : string.Join(Environment.NewLine, ports));
    return 0;
}

static async Task<int> Detect()
{
    Console.WriteLine("Probing serial ports...");
    var found = await DmxAdapterDetector.DetectAsync();
    Console.WriteLine(found.Count == 0 ? "No USB DMX adapters found." : string.Join(Environment.NewLine, found));
    return 0;
}

static ILoggerFactory CreateLoggerFactory() => LoggerFactory.Create(builder => builder
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    }));

/// <summary>--silent: say which sound would have played, and when it stops, instead of playing it.</summary>
sealed class ConsoleAudioOutput : IAudioOutput
{
    readonly Dictionary<Guid, string> _playing = new();

    public void Start(AudioCue cue, TimeSpan fileOffset, double volume)
    {
        _playing[cue.EventId] = cue.Name;
        Console.WriteLine($"[audio] start {cue.Name} at {fileOffset.TotalSeconds:0.00} s in, volume {volume:P0}");
    }

    public void Stop(Guid eventId)
    {
        if (_playing.Remove(eventId, out var name)) Console.WriteLine($"[audio] stop {name}");
    }

    public void StopAll()
    {
        foreach (var id in _playing.Keys.ToList()) Stop(id);
    }

    public void Seek(Guid eventId, TimeSpan fileOffset) { }
    public void Pause(Guid eventId) { }
    public void Resume(Guid eventId) { }
    public void SetVolume(Guid eventId, double volume) { }
    public AudioInstanceState GetState(Guid eventId) => _playing.ContainsKey(eventId) ? AudioInstanceState.Playing : AudioInstanceState.None;
    public TimeSpan? GetLength(string path) => null;
    public int Generation => 0;
}

/// <summary>--dry: prints the non-zero channels of each universe's output four times a second when they change.</summary>
sealed class ConsoleFramePrinter : IDisposable
{
    readonly Timer _timer;
    readonly Dictionary<int, string> _lastPrinted = new();

    public ConsoleFramePrinter(OutputManager outputs)
    {
        var services = outputs.Services;
        var frame = new byte[512];
        _timer = new Timer(_ =>
        {
            foreach (var service in services)
            {
                service.CopyLastSentFrame(frame);
                var active = frame.Select((value, i) => (Address: i + 1, Value: value)).Where(c => c.Value != 0);
                string line = string.Join(" ", active.Select(c => $"{c.Address}={c.Value}"));
                if (_lastPrinted.GetValueOrDefault(service.Universe) == line) continue;
                _lastPrinted[service.Universe] = line;
                Console.WriteLine($"U{service.Universe}: {(line.Length == 0 ? "(blackout)" : line)}");
            }
        }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
    }

    public void Dispose() => _timer.Dispose();
}
