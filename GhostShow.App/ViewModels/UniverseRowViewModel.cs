using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GhostShow.Core.Model;
using GhostShow.Core.Storage;
using GhostShow.Dmx;
using GhostShow.Dmx.Discovery;

namespace GhostShow.App.ViewModels;

/// <summary>An entry in a universe's device picker.</summary>
public sealed record DeviceChoice(string Label, OutputDeviceConfig Config)
{
    public override string ToString() => Label;
}

/// <summary>
/// One universe: its name and number (part of the show) and the device it goes out on (part of this machine's
/// settings), plus the live status of its output.
/// </summary>
public sealed partial class UniverseRowViewModel : ObservableObject
{
    readonly Action _showChanged;
    readonly Action<UniverseRowViewModel> _deviceChanged;
    bool _refreshingChoices;

    public UniverseRowViewModel(UniverseConfig model, Action showChanged, Action<UniverseRowViewModel> deviceChanged)
    {
        Model = model;
        _showChanged = showChanged;
        _deviceChanged = deviceChanged;
    }

    public UniverseConfig Model { get; }

    public ObservableCollection<DeviceChoice> Choices { get; } = new();

    public decimal Number
    {
        get => Model.Number;
        set
        {
            if ((int)value == Model.Number) return;
            Model.Number = (int)value;
            OnPropertyChanged();
            _showChanged();
        }
    }

    public string Name
    {
        get => Model.Name;
        set
        {
            if (value == Model.Name) return;
            Model.Name = value;
            OnPropertyChanged();
            _showChanged();
        }
    }

    [ObservableProperty]
    public partial DeviceChoice? SelectedDevice { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial bool IsFaulted { get; set; }

    partial void OnSelectedDeviceChanged(DeviceChoice? value)
    {
        // The ComboBox clears its selection while Choices is rebuilt; that isn't the user picking something.
        if (_refreshingChoices || value is null) return;
        _deviceChanged(this);
    }

    /// <summary>
    /// Offers the simulator, every adapter just detected, and the currently assigned device even if it wasn't
    /// detected (it may be unplugged, or busy because this universe's own output has it open).
    /// </summary>
    public void RefreshChoices(IReadOnlyList<DmxAdapterInfo> adapters, OutputDeviceConfig assigned)
    {
        _refreshingChoices = true;
        try
        {
            Choices.Clear();
            Choices.Add(new DeviceChoice("Simulator (no hardware)", new SimulatorDeviceConfig()));
            foreach (var adapter in adapters) Choices.Add(new DeviceChoice(adapter.ToString(), ToConfig(adapter)));

            var match = Choices.FirstOrDefault(c => SameDevice(c.Config, assigned));
            if (match is null)
            {
                match = new DeviceChoice($"{Describe(assigned)} (assigned; not detected)", assigned);
                Choices.Add(match);
            }
            SelectedDevice = match;
        }
        finally { _refreshingChoices = false; }
    }

    public void ShowStatus(DmxOutputService? service)
    {
        if (service is null)
        {
            Status = "Not running";
            IsFaulted = false;
            return;
        }
        var timing = service.Timing;
        Status = service.Status switch
        {
            DmxOutputStatus.Connected => $"Connected · {timing.FramesPerSecond:0.0} frames/s",
            DmxOutputStatus.Faulted => $"Fault: {service.LastError} (retrying)",
            var other => other.ToString(),
        };
        IsFaulted = service.Status == DmxOutputStatus.Faulted;
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
        _ => "Simulator (no hardware)",
    };
}
