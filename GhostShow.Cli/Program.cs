using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Model;
using GhostShow.Core.Output;
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
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine($"""
        ghostshow demo   <show.json>             write a sample show
        ghostshow ports                          list serial ports
        ghostshow detect                         find USB DMX adapters (Pro and Open DMX) and their serial numbers
        ghostshow run    <show.json> <scene> [output] [--silent]
                                                 play a timeline scene (prototype; frozen for Phase 1)
        ghostshow soak   [--minutes N] [--opendmx PORT | --usb PORT] [--dark] [--report FILE]
                                                 sustained-output test (default 30 min on the simulator);
                                                 --dark keeps fixtures at 0 so it's safe on a live rig

        Output: by default each universe goes to the device assigned on this machine (set in the app; stored in
        {AppPaths.Default.MachineSettingsFile}), or to the simulator. To override for one run:
          --opendmx PORT   Open DMX (bare FTDI) adapter, e.g. /dev/ttyUSB0, COM3
          --usb PORT       Enttec Pro-compatible adapter
          --ip A.B.C.D     Art-Net node (use 255.255.255.255 to broadcast)
          --dry            simulator, printing channel values to the console
          --universe N     which universe the override applies to (default 0)
          --silent         don't play audio (otherwise needs ffplay from FFmpeg)
        """);
    return 1;
}

static async Task<int> Demo(string path)
{
    var strobe = new ChannelTrack { Name = "White strobe", Universe = 0, Channel = 1 };
    foreach (var (t, v) in new[] { (0, 0), (2000, 255), (2080, 0), (2200, 255), (2260, 0) })
        strobe.SetKeyframe(t, (byte)v, Interpolation.Step);

    var red = new ChannelTrack { Name = "Red glow", Universe = 0, Channel = 2 };
    red.SetKeyframe(0, 20);
    red.SetKeyframe(4000, 120);
    red.SetKeyframe(8000, 20);

    var sound = new AudioTrack { Name = "Sound" };
    sound.AddClip(new AudioClip(1950, "audio/thunder.wav", 100));

    var scene = new Scene { Name = "Lightning Room", DurationMs = 8000, Loop = true };
    scene.Tracks.AddRange([strobe, red, sound]);

    var project = new Project
    {
        Name = "Demo Haunt",
        Fixtures =
        [
            new FixtureInstance { Name = "Strobe", ProfileId = "generic/dimmer", ModeName = "1 channel", StartAddress = 1 },
            new FixtureInstance { Name = "Red glow", ProfileId = "generic/dimmer", ModeName = "1 channel", StartAddress = 2 },
        ],
        Scenes = [scene],
    };
    await ProjectStore.SaveAsync(project, path);
    Console.WriteLine($"Wrote {path}. Put a thunder.wav in an 'audio' folder next to it, then:");
    Console.WriteLine($"  ghostshow run {path} \"Lightning Room\" --dry");
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

    var scene = session.Project.FindScene(args[2]);
    if (scene is null)
    {
        Console.Error.WriteLine($"No scene '{args[2]}'. Available: {string.Join(", ", session.Project.Scenes.Select(s => s.Name))}");
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
    using IAudioPlayer audio = args.Contains("--silent") ? new ConsoleAudioPlayer() : new FfplayAudioPlayer();
    using var runner = new SceneRunner(scene, new EngineFrameSink(engine), audio, Path.GetDirectoryName(Path.GetFullPath(path))!);

    Console.CancelKeyPress += (_, e) => { e.Cancel = true; runner.Stop(); };
    Console.WriteLine($"Running '{scene.Name}' ({(scene.Loop ? "looping" : $"{scene.DurationMs} ms")}). Ctrl+C to stop.");
    runner.Start();
    runner.Wait();
    bool stoppedCleanly = await outputs.StopAllAsync();
    Console.WriteLine(stoppedCleanly ? "Stopped (blackout sent)." : "Stopped, but an output didn't shut down in time; see the log.");
    return stoppedCleanly ? 0 : 3;
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
    int universe = 0;
    if (universeArg is not null && !int.TryParse(universeArg, out universe)) return $"--universe needs a number, not '{universeArg}'.";

    OutputDeviceConfig? device = null;
    if (args.Contains("--dry")) device = new SimulatorDeviceConfig();
    else if (ArgValue(args, "--opendmx") is { } openDmxPort) device = new OpenDmxDeviceConfig(openDmxPort, null);
    else if (ArgValue(args, "--usb") is { } proPort) device = new UsbDmxProDeviceConfig(proPort, null);
    else if (ArgValue(args, "--ip") is { } ip) device = new ArtNetDeviceConfig(ip == "255.255.255.255" ? null : ip, universe);

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

/// <summary>--silent: say which sound would have played instead of playing it.</summary>
sealed class ConsoleAudioPlayer : IAudioPlayer
{
    public void Play(string file, int volume) => Console.WriteLine($"[audio] {file} @ {volume}%");
    public void StopAll() { }
    public void Dispose() { }
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
