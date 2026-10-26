using GhostShow.Core.Engine;
using GhostShow.Core.Storage;
using GhostShow.Dmx.Devices;
using Microsoft.Extensions.Logging;

namespace GhostShow.Dmx;

/// <summary>
/// Keeps one <see cref="DmxOutputService"/> running for each universe in the show, on the device machine settings
/// assign to it, or on the simulator if none is assigned. Re-applying only restarts outputs whose device changed,
/// so editing one universe's hardware doesn't blink the others.
///
/// Not thread-safe: call from one thread (UI or CLI main). The services themselves run on their own threads.
/// </summary>
public sealed class OutputManager : IAsyncDisposable
{
    static readonly SimulatorDeviceConfig Simulator = new();

    readonly ShowEngine _engine;
    readonly ILoggerFactory _loggerFactory;
    readonly DmxOutputOptions _options;
    readonly Func<OutputDeviceConfig, IDmxOutputDevice> _createDevice;
    readonly SortedDictionary<int, (OutputDeviceConfig Config, DmxOutputService Service)> _running = new();

    public OutputManager(ShowEngine engine, ILoggerFactory loggerFactory, DmxOutputOptions? options = null,
                         Func<OutputDeviceConfig, IDmxOutputDevice>? createDevice = null)
    {
        _engine = engine;
        _loggerFactory = loggerFactory;
        _options = options ?? new DmxOutputOptions();
        _createDevice = createDevice ?? CreateDevice;
    }

    /// <summary>Running services, by universe.</summary>
    public IReadOnlyList<DmxOutputService> Services => _running.Values.Select(r => r.Service).ToArray();

    public DmxOutputService? Find(int universe) => _running.GetValueOrDefault(universe).Service;

    /// <summary>Raised after Apply or Stop changes which services are running.</summary>
    public event Action? ServicesChanged;

    public async Task ApplyAsync(IReadOnlyCollection<int> universes, MachineSettings settings)
    {
        var wanted = universes.Distinct().ToDictionary(u => u, u => settings.FindOutput(u) ?? Simulator);

        // Stop first: a device that moves to another universe must release its port before it can be reopened.
        var toStop = _running.Where(r => !wanted.TryGetValue(r.Key, out var config) || config != r.Value.Config)
                             .Select(r => r.Key).ToList();
        foreach (var universe in toStop)
        {
            await _running[universe].Service.StopAsync();
            _running.Remove(universe);
        }

        var toStart = wanted.Where(w => !_running.ContainsKey(w.Key)).ToList();
        foreach (var (universe, config) in toStart)
        {
            var service = new DmxOutputService(_engine, universe, _createDevice(config),
                                               _loggerFactory.CreateLogger<DmxOutputService>(), _options);
            _running[universe] = (config, service);
            service.Start();
        }

        if (toStop.Count > 0 || toStart.Count > 0) ServicesChanged?.Invoke();
    }

    /// <summary>Stops every output (each sends its blackout first). Returns false if any didn't stop in time.</summary>
    public async Task<bool> StopAllAsync()
    {
        var results = await Task.WhenAll(_running.Values.Select(r => r.Service.StopAsync()));
        _running.Clear();
        ServicesChanged?.Invoke();
        return results.All(stopped => stopped);
    }

    public async ValueTask DisposeAsync() => await StopAllAsync();

    public static IDmxOutputDevice CreateDevice(OutputDeviceConfig config) => config switch
    {
        SimulatorDeviceConfig => new SimulatorDevice(),
        OpenDmxDeviceConfig c => new OpenDmxDevice(c.Port, c.SerialNumber),
        UsbDmxProDeviceConfig c => new UsbProDevice(c.Port, c.SerialNumber),
        ArtNetDeviceConfig c => new ArtNetDevice(c.Address, c.ArtNetUniverse),
        _ => throw new ArgumentException($"Unknown output device type {config.GetType().Name}.", nameof(config)),
    };
}
