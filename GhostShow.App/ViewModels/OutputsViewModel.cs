using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Engine;
using GhostShow.Core.Model;
using GhostShow.Core.Storage;
using GhostShow.Dmx;
using GhostShow.Dmx.Discovery;

namespace GhostShow.App.ViewModels;

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
    public ObservableCollection<DmxAdapterInfo> Adapters { get; } = new();

    [ObservableProperty]
    public partial string DetectStatus { get; set; } = "Not scanned yet.";

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
    /// Scans USB serial ports. Runs on a worker thread; probing takes about a second. Adapters open in a running
    /// output can't be probed and don't appear, but stay selectable as their universe's assigned device.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task Detect()
    {
        DetectStatus = "Scanning USB serial ports…";
        _adapters = await DmxAdapterDetector.DetectAsync();
        Adapters.Clear();
        foreach (var adapter in _adapters) Adapters.Add(adapter);
        DetectStatus = _adapters.Count switch
        {
            0 => "No free adapters found. Adapters already in use by an output aren't listed. On Linux, you need to be in the dialout group.",
            1 => "Found 1 free adapter.",
            var n => $"Found {n} free adapters.",
        };
        RefreshChoices();
    }

    [RelayCommand]
    async Task AddUniverse()
    {
        int next = _session.Project.Universes.Select(u => u.Number).DefaultIfEmpty(-1).Max() + 1;
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
