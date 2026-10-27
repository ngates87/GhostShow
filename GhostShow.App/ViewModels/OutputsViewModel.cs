using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Engine;
using GhostShow.Core.Model;
using GhostShow.Core.Storage;
using GhostShow.Dmx;
using GhostShow.Dmx.Discovery;

namespace GhostShow.App.ViewModels;

/// <summary>A line in the adapter list: what it is, and whether it's free or which universe has it.</summary>
public sealed record AdapterRow(string Description, string Status)
{
    public override string ToString() => $"{Description}  ·  {Status}";
}

/// <summary>The show's universes and, for this machine, which device each one is output on.</summary>
public sealed partial class OutputsViewModel : ObservableObject
{
    static readonly SimulatorDeviceConfig Simulator = new();

    readonly ShowSession _session;
    readonly OutputManager _outputs;
    readonly MachineSettings _settings;
    readonly Func<Task> _saveSettings;
    readonly Action _markDirty;
    IReadOnlyList<DmxAdapterInfo> _adapters = [];
    // Probe results by port: an adapter, or null for "checked, not an adapter". Busy ports are left out (see Watch).
    readonly Dictionary<string, DmxAdapterInfo?> _probed = new();
    bool _watching;

    public OutputsViewModel(ShowSession session, OutputManager outputs, MachineSettings settings,
                            Func<Task> saveSettings, Action markDirty)
    {
        _session = session;
        _outputs = outputs;
        _settings = settings;
        _saveSettings = saveSettings;
        _markDirty = markDirty;
        Reload();
    }

    public ObservableCollection<UniverseRowViewModel> Universes { get; } = new();
    public ObservableCollection<AdapterRow> Adapters { get; } = new();

    [ObservableProperty]
    public partial string DetectStatus { get; set; } = "Looking for adapters…";

    /// <summary>Rebuilds the rows from the show, e.g. after New or Open.</summary>
    public void Reload()
    {
        Universes.Clear();
        foreach (var universe in _session.Project.Universes)
            Universes.Add(new UniverseRowViewModel(universe, OnShowUniversesChanged, OnDeviceChanged));
        RefreshChoices();
        Tick();
    }

    /// <summary>Starts, stops or restarts outputs to match the show's universes and this machine's assignments.</summary>
    public Task ApplyOutputsAsync() => _outputs.ApplyAsync(_session.Engine.Universes, _settings);

    /// <summary>
    /// Keeps the adapter list current while the DMX setup screen is open; call every couple of seconds. Listing ports
    /// opens none of them, so it's cheap. Only ports not seen before are probed (which briefly opens them and sends a
    /// request), so plugging an adapter in shows it within a few seconds without re-probing everything. Ports held by
    /// a running output can't be opened (the OS gives exclusive access), so probing never disturbs live output.
    /// </summary>
    public async Task WatchAdaptersAsync()
    {
        if (_watching) return;
        _watching = true;
        try
        {
            var ports = await Task.Run(DmxAdapterDetector.CandidatePorts);
            var gone = _probed.Keys.Except(ports).ToList();
            var fresh = ports.Where(p => !_probed.ContainsKey(p)).ToList();
            foreach (var port in gone) _probed.Remove(port);

            if (fresh.Count > 0)
            {
                DetectStatus = "Checking newly connected USB serial ports…";
                var results = await Task.WhenAll(fresh.Select(port => Task.Run(() =>
                    (Port: port, Adapter: DmxAdapterDetector.Probe(port, 400, out bool busy), Busy: busy))));
                // A busy port isn't remembered: it may be an adapter that's in use now and free later.
                foreach (var result in results.Where(r => !r.Busy)) _probed[result.Port] = result.Adapter;
            }

            _adapters = _probed.Values.OfType<DmxAdapterInfo>().OrderBy(a => a.Port, StringComparer.OrdinalIgnoreCase).ToArray();
            ShowAdapters();
            if (gone.Count > 0 || fresh.Count > 0) RefreshChoices();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Runs unattended in the background, so report rather than throw into nowhere.
            DetectStatus = $"Couldn't check for adapters: {ex.Message}";
        }
        finally
        {
            _watching = false;
        }
    }

    /// <summary>Forgets every earlier probe and checks all ports again.</summary>
    [RelayCommand]
    async Task ScanAgain()
    {
        _probed.Clear();
        await WatchAdaptersAsync();
    }

    /// <summary>Free adapters from probing, plus the ones running outputs have open (which can't be probed).</summary>
    void ShowAdapters()
    {
        var rows = _adapters.Select(a => new AdapterRow(a.ToString(), "Available")).ToList();
        foreach (var service in _outputs.Services.Where(s => s.Device.Port is not null && s.Status == DmxOutputStatus.Connected))
            rows.Add(new AdapterRow(service.Device.ToString(), $"In use: universe {service.Universe}"));

        if (!rows.SequenceEqual(Adapters))
        {
            Adapters.Clear();
            foreach (var row in rows) Adapters.Add(row);
        }
        DetectStatus = rows.Count == 0
            ? "No adapters connected. Plug one in: it appears here within a few seconds. (On Linux, you need to be in the dialout group.)"
            : "Watching for adapters being plugged in or out.";
    }

    [RelayCommand]
    async Task AddUniverse()
    {
        int next = _session.Project.Universes.Select(u => u.Number).DefaultIfEmpty(GhostShow.Core.Model.Project.FirstUniverse - 1).Max() + 1;
        var universe = new UniverseConfig { Number = next, Name = $"Universe {next}" };
        _session.Project.Universes.Add(universe);
        var row = new UniverseRowViewModel(universe, OnShowUniversesChanged, OnDeviceChanged);
        row.RefreshChoices(_adapters, Simulator);
        Universes.Add(row);
        await ShowUniversesChangedAsync();
    }

    [RelayCommand]
    async Task RemoveUniverse(UniverseRowViewModel row)
    {
        _session.Project.Universes.Remove(row.Model);
        Universes.Remove(row);
        await ShowUniversesChangedAsync();
    }

    public void Tick()
    {
        foreach (var row in Universes) row.ShowStatus(_outputs.Find(row.Model.Number));
        if (!_watching) ShowAdapters();   // an output connecting or dropping changes which adapters are "in use"
    }

    void RefreshChoices()
    {
        foreach (var row in Universes) row.RefreshChoices(_adapters, _settings.FindOutput(row.Model.Number) ?? Simulator);
    }

    async void OnShowUniversesChanged() => await ShowUniversesChangedAsync();

    async Task ShowUniversesChangedAsync()
    {
        _markDirty();
        _session.Apply();
        RefreshChoices();   // a renumbered universe picks up that number's assignment
        await ApplyOutputsAsync();
        Tick();
    }

    async void OnDeviceChanged(UniverseRowViewModel row)
    {
        var device = row.SelectedDevice!.Config;
        _settings.SetOutput(row.Model.Number, device is SimulatorDeviceConfig ? null : device);
        await ApplyOutputsAsync();
        Tick();
        await _saveSettings();
    }
}
