using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Storage;
using GhostShow.Inputs;

namespace GhostShow.App.ViewModels;

/// <summary>A serial port to choose; Port null means "none".</summary>
public sealed record SerialPortChoice(string Label, string? Port)
{
    public override string ToString() => Label;
}

/// <summary>
/// Inputs → Serial input: which serial port a sensor box or prop controller sends its commands on, and at what speed.
/// This computer's settings (like DMX adapters), not the show's: the show only stores the commands its buttons answer.
/// Only the port picked is ever opened. A port a DMX adapter is using isn't opened: it says so instead.
/// </summary>
public sealed partial class SerialInputViewModel : ObservableObject
{
    /// <summary>The usual speeds; 9600 is the most common for sensor boxes.</summary>
    public static IReadOnlyList<int> BaudRates { get; } = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];

    static readonly SerialPortChoice NoPort = new("None", null);

    readonly SerialInputService _service;
    readonly MachineSettings _settings;
    readonly Func<Task> _saveSettings;
    readonly Func<IEnumerable<(int Universe, string Port)>> _dmxPorts;
    readonly Func<IReadOnlyList<string>> _listPorts;
    bool _updating;

    /// <param name="dmxPorts">The serial ports DMX adapters are on now, by universe.</param>
    /// <param name="listPorts">The ports this computer has (tests pass their own).</param>
    public SerialInputViewModel(SerialInputService service, MachineSettings settings, Func<Task> saveSettings,
                                Func<IEnumerable<(int Universe, string Port)>> dmxPorts, Func<IReadOnlyList<string>>? listPorts = null)
    {
        _service = service;
        _settings = settings;
        _saveSettings = saveSettings;
        _dmxPorts = dmxPorts;
        _listPorts = listPorts ?? SerialPortLink.PortNames;

        _updating = true;
        Ports.Add(NoPort);
        if (settings.SerialInput.Port is { } saved) Ports.Add(new SerialPortChoice(saved, saved));
        SelectedPort = Ports[^1];
        BaudRate = settings.SerialInput.BaudRate;
        IsEnabled = settings.SerialInput.Enabled;
        _updating = false;
        Refresh();
    }

    public ObservableCollection<SerialPortChoice> Ports { get; } = new();

    [ObservableProperty]
    public partial SerialPortChoice? SelectedPort { get; set; }

    [ObservableProperty]
    public partial int BaudRate { get; set; }

    /// <summary>Off keeps the port picked but closed, e.g. to let another program use it.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    /// <summary>"Listening on COM3 at 9600 baud", "COM3 isn't connected. Trying again every few seconds."</summary>
    [ObservableProperty]
    public partial string StatusLabel { get; private set; } = "";

    /// <summary>The port is picked and on, but not being listened to (missing, in use, or a DMX adapter's).</summary>
    [ObservableProperty]
    public partial bool IsFaulted { get; private set; }

    /// <summary>Commands are being read: a button waiting for a trigger can learn one.</summary>
    [ObservableProperty]
    public partial bool IsListening { get; private set; }

    /// <summary>For the toolbar's warnings: what's wrong with the serial input, or null.</summary>
    public string? Warning => IsFaulted ? $"Serial input: {StatusLabel} See Inputs." : null;

    partial void OnSelectedPortChanged(SerialPortChoice? value) => SettingsChanged();
    partial void OnBaudRateChanged(int value) => SettingsChanged();
    partial void OnIsEnabledChanged(bool value) => SettingsChanged();

    void SettingsChanged()
    {
        if (_updating) return;
        var chosen = new SerialInputSettings(SelectedPort?.Port, BaudRate, IsEnabled);
        if (chosen == _settings.SerialInput) return;
        _settings.SerialInput = chosen;
        _ = _saveSettings();
        Apply();
    }

    /// <summary>Lists the ports again (after plugging in a USB-serial cable). The port picked stays, even if unplugged.</summary>
    [RelayCommand]
    void RefreshPorts()
    {
        var names = _listPorts();
        _updating = true;
        try
        {
            string? selected = SelectedPort?.Port;
            Ports.Clear();
            Ports.Add(NoPort);
            foreach (var name in names) Ports.Add(new SerialPortChoice(Label(name), name));
            if (selected is not null && !names.Contains(selected)) Ports.Add(new SerialPortChoice($"{selected} (not connected)", selected));
            SelectedPort = Ports.FirstOrDefault(p => p.Port == selected) ?? NoPort;
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>"COM3", or "COM4 (DMX universe 1)" for a port a DMX adapter is on.</summary>
    string Label(string port) => DmxUniverseOn(port) is { } universe ? $"{port} (DMX universe {universe})" : port;

    int? DmxUniverseOn(string port)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var (universe, dmxPort) in _dmxPorts())
            if (string.Equals(dmxPort, port, comparison)) return universe;
        return null;
    }

    /// <summary>Starts or stops listening to match the settings. Called at startup, on a change, and once a second.</summary>
    public void Apply()
    {
        var (port, baud, enabled) = _settings.SerialInput;
        if (port is not null && enabled && DmxUniverseOn(port) is null) _service.Start(port, baud);
        else _service.Stop();
        Refresh();
    }

    /// <summary>Shows how the serial input stands. The owner calls it, on the UI thread, when the service's status changes.</summary>
    public void Refresh()
    {
        var (port, _, enabled) = _settings.SerialInput;
        var status = _service.Status;
        int? dmx = port is null ? null : DmxUniverseOn(port);
        (StatusLabel, IsFaulted) = (port, enabled, dmx, status.State) switch
        {
            (null, _, _, _) => ("Off: pick the port your device is on.", false),
            (_, false, _, _) => ($"Off: {port} isn't being listened to.", false),
            (_, _, { } universe, _) => ($"{port} is DMX universe {universe}'s adapter: pick another port.", true),
            (_, _, _, SerialInputState.Listening) => ($"Listening on {port} at {status.BaudRate} baud.", false),
            (_, _, _, SerialInputState.Retrying) => ($"{status.Problem} Trying again every few seconds.", true),
            _ => ($"Opening {port}…", false),
        };
        IsListening = status.State == SerialInputState.Listening;
        OnPropertyChanged(nameof(Warning));
    }

    /// <summary>Once a second: a DMX adapter may have moved onto (or off) the port.</summary>
    public void Tick()
    {
        if (_settings.SerialInput is not { Port: { } port, Enabled: true }) return;
        bool blocked = DmxUniverseOn(port) is not null;
        bool running = _service.Status.State != SerialInputState.Off;
        if (blocked == running) Apply();
    }
}
