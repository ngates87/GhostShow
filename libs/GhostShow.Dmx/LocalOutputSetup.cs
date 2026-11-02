using GhostShow.Core.Dmx;
using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Storage;
using GhostShow.Dmx.Discovery;

namespace GhostShow.Dmx;

/// <summary>
/// The DMX hardware on this computer, for the DMX setup screen on any device: finds adapters while a screen is open,
/// keeps an output running for each universe (<see cref="OutputManager"/>), and remembers which device each one uses
/// (this machine's settings, not the show). Call everything on the show's thread.
/// </summary>
public sealed class LocalOutputSetup : IOutputSetup
{
    const string SimulatorLabel = "Simulator (no hardware)";
    static readonly SimulatorDeviceConfig Simulator = new();

    readonly ShowSession _session;
    readonly OutputManager _outputs;
    readonly MachineSettings _settings;
    readonly Func<Task> _saveSettings;
    readonly IDmxAdapterFinder _finder;
    readonly HashSet<object> _watchers = [];
    // Probe results by port: an adapter, or null for "checked, not an adapter". Busy ports are left out (see FindAdaptersAsync).
    readonly Dictionary<string, DmxAdapterInfo?> _probed = new();
    IReadOnlyList<DmxAdapterInfo> _adapters = [];
    bool _finding;
    bool _foundOnce;
    bool _checkingNewPorts;
    string? _problem;
    int _ticks;

    public LocalOutputSetup(ShowSession session, OutputManager outputs, MachineSettings settings, Func<Task> saveSettings, IDmxAdapterFinder? finder = null)
    {
        _session = session;
        _outputs = outputs;
        _settings = settings;
        _saveSettings = saveSettings;
        _finder = finder ?? new SerialPortAdapterFinder();
        session.Document.Changed += OnShowChanged;
        Refresh();
    }

    public OutputSetupState State { get; private set; } = OutputSetupState.Empty;

    public event Action? Changed;

    /// <summary>The outputs catching up with the latest universe edit; tests wait for it.</summary>
    public Task LastSync { get; private set; } = Task.CompletedTask;

    /// <summary>Starts, stops or restarts outputs to match the show's universes and this machine's assignments.</summary>
    public async Task ApplyAsync()
    {
        await _outputs.ApplyAsync(_session.Engine.Universes, _settings);
        Refresh();
    }

    public void SetWatching(object screen, bool watching)
    {
        bool firstScreen = watching && _watchers.Count == 0;
        if (watching) _watchers.Add(screen);
        else _watchers.Remove(screen);
        if (firstScreen) _ = FindAdaptersAsync();   // as soon as the screen opens
    }

    /// <summary>Once a second: outputs' status, and every 2 s while a screen is open, adapters plugged in or out.</summary>
    public void Tick()
    {
        Refresh();
        if (_watchers.Count > 0 && ++_ticks % 2 == 0) _ = FindAdaptersAsync();
    }

    public async Task ScanAgainAsync()
    {
        _probed.Clear();
        await FindAdaptersAsync();
    }

    public async Task<string?> AssignAsync(int universe, string choice)
    {
        if (_session.Project.Universes.All(u => u.Number != universe)) return $"The show has no universe {universe}.";
        var assigned = _settings.FindOutput(universe) ?? Simulator;
        var picked = ChoicesFor(universe, assigned).FirstOrDefault(c => c.Label == choice);
        if (picked.Config is null) return $"'{choice}' isn't available on the show computer any more.";
        if (SameDevice(picked.Config, assigned)) return null;

        _settings.SetOutput(universe, picked.Config is SimulatorDeviceConfig ? null : picked.Config);
        await ApplyAsync();
        await _saveSettings();
        return null;
    }

    /// <summary>
    /// Keeps the adapter list current. Listing ports opens none of them, so it's cheap. Only ports not seen before are
    /// probed (which briefly opens them and sends a request), so plugging an adapter in shows it within a few seconds
    /// without re-probing everything. Ports held by a running output can't be opened (the OS gives exclusive access), so
    /// probing never disturbs live output.
    /// </summary>
    async Task FindAdaptersAsync()
    {
        if (_finding) return;
        _finding = true;
        try
        {
            var ports = await Task.Run(_finder.CandidatePorts);
            var gone = _probed.Keys.Except(ports).ToList();
            var fresh = ports.Where(p => !_probed.ContainsKey(p)).ToList();
            foreach (var port in gone) _probed.Remove(port);

            if (fresh.Count > 0)
            {
                _checkingNewPorts = true;
                Refresh();
                var results = await Task.WhenAll(fresh.Select(port => Task.Run(() =>
                    (Port: port, Adapter: _finder.Probe(port, 400, out bool busy), Busy: busy))));
                // A busy port isn't remembered: it may be an adapter that's in use now and free later.
                foreach (var result in results.Where(r => !r.Busy)) _probed[result.Port] = result.Adapter;
            }

            _adapters = _probed.Values.OfType<DmxAdapterInfo>().OrderBy(a => a.Port, StringComparer.OrdinalIgnoreCase).ToArray();
            _problem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Runs unattended in the background, so report rather than throw into nowhere.
            _problem = $"Couldn't check for adapters: {ex.Message}";
        }
        finally
        {
            _finding = false;
            _checkingNewPorts = false;
            _foundOnce = true;
        }
        Refresh();
    }

    /// <summary>Works out what the screens should show, and tells them if it changed.</summary>
    public void Refresh()
    {
        // Free adapters from probing, plus the ones running outputs have open (which can't be probed).
        var adapters = _adapters.Select(a => new AdapterRow(a.ToString(), "Available")).ToList();
        foreach (var service in _outputs.Services.Where(s => s.Device.Port is not null && s.Status == DmxOutputStatus.Connected))
            adapters.Add(new AdapterRow(service.Device.ToString(), $"In use: universe {service.Universe}"));

        string detectStatus = _problem
            ?? (_checkingNewPorts ? "Checking newly connected USB serial ports…"
            : !_foundOnce ? OutputSetupState.Empty.DetectStatus
            : adapters.Count == 0 ? "No adapters connected. Plug one in: it appears here within a few seconds. (On Linux, you need to be in the dialout group.)"
            : "Watching for adapters being plugged in or out.");

        var universes = _session.Project.Universes.Select(u => OutputOf(u.Number)).ToList();
        var state = new OutputSetupState(detectStatus, adapters, universes);
        if (state.SameAs(State)) return;
        State = state;
        Changed?.Invoke();
    }

    UniverseOutput OutputOf(int universe)
    {
        var assigned = _settings.FindOutput(universe) ?? Simulator;
        var choices = ChoicesFor(universe, assigned);
        var (status, isFaulted) = DescribeStatus(_outputs.Find(universe));
        string selected = choices.First(c => SameDevice(c.Config, assigned)).Label;
        return new UniverseOutput(universe, status, isFaulted, [.. choices.Select(c => c.Label)], selected);
    }

    /// <summary>
    /// Offers the simulator, every adapter just detected, and the currently assigned device even if it wasn't
    /// detected (it may be unplugged, or busy because this universe's own output has it open).
    /// </summary>
    List<(string Label, OutputDeviceConfig Config)> ChoicesFor(int universe, OutputDeviceConfig assigned)
    {
        var choices = new List<(string Label, OutputDeviceConfig Config)> { (SimulatorLabel, Simulator) };
        foreach (var adapter in _adapters) choices.Add((adapter.ToString(), ToConfig(adapter)));
        if (!choices.Any(c => SameDevice(c.Config, assigned))) choices.Add(($"{Describe(assigned)} (assigned; not detected)", assigned));
        return choices;
    }

    static (string Status, bool IsFaulted) DescribeStatus(DmxOutputService? service)
    {
        if (service is null) return ("Not running", false);
        return (service.Status switch
        {
            DmxOutputStatus.Connected => $"Connected · {service.Timing.FramesPerSecond:0.0} frames/s",
            DmxOutputStatus.Faulted => $"Fault: {service.LastError} (retrying)",
            var other => other.ToString(),
        }, service.Status == DmxOutputStatus.Faulted);
    }

    async void OnShowChanged(ShowChange change)
    {
        if (!change.Touches("universes")) return;
        await (LastSync = ApplyAsync());   // a renumbered universe picks up that number's assignment too
    }

    static OutputDeviceConfig ToConfig(DmxAdapterInfo adapter) => adapter.Kind == DmxAdapterKind.OpenDmx
        ? new OpenDmxDeviceConfig(adapter.Port, adapter.SerialNumber)
        : new UsbDmxProDeviceConfig(adapter.Port, adapter.SerialNumber);

    /// <summary>USB adapters are the same device if their serial numbers match, wherever they're plugged in.</summary>
    static bool SameDevice(OutputDeviceConfig a, OutputDeviceConfig b) => (a, b) switch
    {
        (OpenDmxDeviceConfig x, OpenDmxDeviceConfig y) when x.SerialNumber is not null => x.SerialNumber == y.SerialNumber,
        (UsbDmxProDeviceConfig x, UsbDmxProDeviceConfig y) when x.SerialNumber is not null => x.SerialNumber == y.SerialNumber,
        _ => a == b,
    };

    static string Describe(OutputDeviceConfig config) => config switch
    {
        OpenDmxDeviceConfig c => $"{c.Port}  (Open DMX, S/N {c.SerialNumber ?? "unknown"})",
        UsbDmxProDeviceConfig c => $"{c.Port}  (USB Pro, S/N {c.SerialNumber ?? "unknown"})",
        ArtNetDeviceConfig c => $"Art-Net {c.Address ?? "broadcast"}, universe {c.ArtNetUniverse}",
        _ => SimulatorLabel,
    };
}
