using GhostShow.Core.Engine;
using GhostShow.Core.Inputs;
using GhostShow.Core.Model;
using GhostShow.Core.Storage;
using GhostShow.Dmx.Discovery;
using GhostShow.Inputs;

namespace GhostShow.App;

/// <summary>
/// The show computer's serial ports, for the Inputs screen (here and on devices, see <see cref="IInputSetup"/>): which
/// ports it has, which one each of the show's input devices is on (this computer's settings), and listening to them.
/// Only ports picked for a device are opened, never probed; a port a DMX adapter has open isn't, and it says so. Called
/// on the UI thread.
/// </summary>
public sealed class LocalInputSetup : IInputSetup
{
    static readonly SerialPortChoice NoPort = new("None", null);
    static readonly StringComparison PortComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    readonly ShowSession _session;
    readonly SerialInputs _serial;
    readonly MachineSettings _settings;
    readonly Func<Task> _saveSettings;
    readonly Func<IEnumerable<(int Universe, string Port)>> _dmxPorts;
    readonly Func<IReadOnlyList<string>> _listPorts;
    readonly Func<string, UsbPortInfo?> _describePort;
    readonly Func<string, string?> _findUsbSerial;
    readonly Dictionary<Guid, (string Command, DateTimeOffset At)> _lastReceived = [];
    IReadOnlyList<string> _ports = [];

    /// <param name="dmxPorts">The serial ports DMX adapters have open now, by universe. Not where an adapter was last
    /// seen: a port name moves to whatever device is plugged in first, so a missing adapter's old port may be the very
    /// device wanted here.</param>
    /// <param name="listPorts">The ports this computer has (tests pass their own).</param>
    /// <param name="describePort">The USB device on a port, or null if the computer won't say (tests pass their own).</param>
    /// <param name="findUsbSerial">The port the USB device with this serial number is on now, or null (tests pass their own).</param>
    public LocalInputSetup(ShowSession session, SerialInputs serial, MachineSettings settings, Func<Task> saveSettings,
                           Func<IEnumerable<(int Universe, string Port)>> dmxPorts,
                           Func<IReadOnlyList<string>>? listPorts = null, Func<string, UsbPortInfo?>? describePort = null,
                           Func<string, string?>? findUsbSerial = null)
    {
        _session = session;
        _serial = serial;
        _settings = settings;
        _saveSettings = saveSettings;
        _dmxPorts = dmxPorts;
        _listPorts = listPorts ?? SerialPortLink.PortNames;
        _describePort = describePort ?? UsbSerialPorts.Describe;
        _findUsbSerial = findUsbSerial ?? UsbSerialPorts.FindPortByUsbSerial;
        // Inputs added or removed here, undone, or changed on another device: listened to to match.
        session.Document.Changed += change =>
        {
            if (change.Touches("inputs")) Apply();
        };
        Reload();
    }

    public InputSetupState State { get; private set; } = InputSetupState.Empty;

    public event Action? Changed;

    /// <summary>A new or opened show: its devices, put on this computer's old serial port if it's an older show's.</summary>
    public void Reload()
    {
        CarryOverOldSerialPort();
        Apply();
    }

    /// <summary>
    /// Before shows listed their inputs, a computer had one serial port (<see cref="MachineSettings.SerialInput"/>). An
    /// older show's serial triggers now listen to a device of the show's (<see cref="LegacySerialTriggers"/>): that
    /// device goes on the old port, at its speed, so the show works as it did. Kept until a show needs it.
    /// </summary>
    void CarryOverOldSerialPort()
    {
        if (_settings.SerialInput is { Port: null }) _settings.SerialInput = null;   // no port was picked: nothing to carry over
        if (_settings.SerialInput is not { Port: { } port } old) return;
        if (_session.Project.Inputs.OfType<SerialInputDevice>().FirstOrDefault(d => BindingOf(d.Id) is null) is not { } device) return;
        device.BaudRate = old.BaudRate;   // the show had no speed of its own: the computer's was the device's
        _settings.InputPorts.Add(new InputPortBinding(device.Id, port, _describePort(port)?.Serial, old.Enabled));
        _settings.SerialInput = null;
        _ = _saveSettings();
    }

    // ---- which port each device is on ----

    InputPortBinding? BindingOf(Guid device) => _settings.InputPorts.FirstOrDefault(b => b.Input == device);

    /// <summary>Puts a device on a port (or none), on this computer, and listens to match.</summary>
    void SetBinding(Guid device, InputPortBinding? binding)
    {
        var old = BindingOf(device);
        if (old == binding) return;
        if (old is not null) _settings.InputPorts.Remove(old);
        if (binding is not null) _settings.InputPorts.Add(binding);
        _ = _saveSettings();
        Apply();
    }

    public string? PickPort(Guid device, string? port)
    {
        var old = BindingOf(device);
        if (port is null) SetBinding(device, null);
        else if (old is null || !string.Equals(old.Port, port, PortComparison))
            SetBinding(device, new InputPortBinding(device, port, _describePort(port)?.Serial, old?.Enabled ?? true));
        return null;
    }

    public string? SetEnabled(Guid device, bool enabled)
    {
        if (BindingOf(device) is { } binding && binding.Enabled != enabled) SetBinding(device, binding with { Enabled = enabled });
        return null;
    }

    public void RefreshPorts()
    {
        _ports = _listPorts();
        Publish();
    }

    /// <summary>"COM5 · FTDI FT232R USB UART": the USB device's maker and product name, when the computer can say.</summary>
    string PortLabel(string port) => HardwareName(_describePort(port)) is { } name ? $"{port} · {name}" : port;

    /// <summary>"FTDI FT232R USB UART": the USB device's maker and product, or null if the computer won't say.</summary>
    static string? HardwareName(UsbPortInfo? usb)
    {
        if (usb is null) return null;
        var name = string.Join(" ", new[] { usb.Manufacturer, usb.Product }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return name.Length > 0 ? name : null;
    }

    /// <summary>What already has this port: "DMX universe 1" (an adapter has it open) or another input of the show.</summary>
    string? UsedBy(string port, Guid? except)
    {
        if (DmxUniverseOn(port) is { } universe) return $"DMX universe {universe}";
        foreach (var device in _session.Project.Inputs)
            if (device.Id != except && BindingOf(device.Id) is { } b && string.Equals(b.Port, port, PortComparison)) return device.Name;
        return null;
    }

    int? DmxUniverseOn(string port)
    {
        foreach (var (universe, dmxPort) in _dmxPorts())
            if (string.Equals(dmxPort, port, PortComparison)) return universe;
        return null;
    }

    /// <summary>Why a device can't be listened to on its port: a DMX adapter or an input before it has it.</summary>
    string? Blocked(InputDevice device, string port)
    {
        if (DmxUniverseOn(port) is { } universe) return $"{port} is DMX universe {universe}'s adapter: pick another port.";
        foreach (var other in _session.Project.Inputs.TakeWhile(i => i != device))
            if (BindingOf(other.Id) is { Enabled: true } b && string.Equals(b.Port, port, PortComparison)) return $"{port} is {other.Name}'s port: pick another.";
        return null;
    }

    // ---- listening ----

    /// <summary>Starts or stops listening to match the show and this computer's settings. At startup, on a change, and when a tick finds a reason.</summary>
    public void Apply()
    {
        var wanted = new List<(Guid, string, int)>();
        foreach (var device in _session.Project.Inputs.OfType<SerialInputDevice>())
            if (BindingOf(device.Id) is { Enabled: true } binding && Blocked(device, binding.Port) is null)
                wanted.Add((device.Id, binding.Port, device.BaudRate));
        _serial.Listen(wanted);
        Publish();
    }

    /// <summary>A port's status changed (the owner calls it, on the UI thread).</summary>
    public void Refresh() => Publish();

    /// <summary>A device sent a command: shown on its card. On the UI thread.</summary>
    public void OnCommand(Guid device, string command, DateTimeOffset at)
    {
        _lastReceived[device] = (command, at);
        Publish();
    }

    /// <summary>
    /// Once a second: a DMX adapter may have moved onto or off a port, and a USB device that isn't answering may have come
    /// back on another port (its serial number finds it there, and it's followed).
    /// </summary>
    public void Tick()
    {
        bool apply = false;
        foreach (var device in _session.Project.Inputs.OfType<SerialInputDevice>())
        {
            if (BindingOf(device.Id) is not { Enabled: true } binding) continue;
            var state = _serial.Status(device.Id).State;
            if ((Blocked(device, binding.Port) is null) != (state != SerialInputState.Off)) apply = true;
            if (state == SerialInputState.Retrying && binding.UsbSerial is { } usb && _findUsbSerial(usb) is { } now
                && !string.Equals(now, binding.Port, PortComparison) && DmxUniverseOn(now) is null)
            {
                _settings.InputPorts[_settings.InputPorts.IndexOf(binding)] = binding with { Port = now };
                _ = _saveSettings();
                apply = true;
            }
        }
        if (apply) Apply();
    }

    // ---- what the screens show ----

    void Publish()
    {
        var found = _ports.Select(p =>
        {
            var usb = _describePort(p);
            return new FoundPort(p, PortLabel(p), UsedBy(p, except: null), HardwareName(usb), usb is not null);
        }).ToList();
        string foundStatus = found.Count == 0 ? "No serial ports on this computer. Plug in the device's USB cable and press Look again." : "";
        var state = new InputSetupState(foundStatus, found, [.. _session.Project.Inputs.Select(DeviceSetup)]);
        if (state.SameAs(State)) return;
        State = state;
        Changed?.Invoke();
    }

    InputDeviceSetup DeviceSetup(InputDevice device)
    {
        var binding = BindingOf(device.Id);
        var usb = binding is null ? null : _describePort(binding.Port);
        var (status, faulted, listening, health) = Status(device);
        var last = _lastReceived.TryGetValue(device.Id, out var l) ? l : ((string, DateTimeOffset)?)null;
        return new InputDeviceSetup(device.Id, binding?.Port, binding?.UsbSerial, binding?.Enabled ?? true, HardwareName(usb), usb is not null,
                                    PortChoices(device.Id), status, faulted, listening, health, last?.Item1, last?.Item2);
    }

    List<SerialPortChoice> PortChoices(Guid device)
    {
        var choices = new List<SerialPortChoice> { NoPort };
        foreach (var port in _ports)
            choices.Add(new SerialPortChoice(UsedBy(port, except: device) is { } user ? $"{PortLabel(port)} ({user})" : PortLabel(port), port));
        if (BindingOf(device) is { } binding && !_ports.Any(p => string.Equals(p, binding.Port, PortComparison)))
            choices.Add(new SerialPortChoice($"{binding.Port} (not connected)", binding.Port));
        return choices;
    }

    /// <summary>
    /// How a device stands, in words and for its status light. In the show but on no port here is grey rather than red:
    /// nothing is wrong with any hardware, a port just needs picking (it's still among the toolbar's warnings).
    /// </summary>
    (string Label, bool Faulted, bool Listening, InputHealth Health) Status(InputDevice device)
    {
        if (BindingOf(device.Id) is not { } binding) return ("Not on a port on this computer: pick the port it's plugged into.", true, false, InputHealth.Inactive);
        if (!binding.Enabled) return ($"Off: {binding.Port} isn't being listened to.", false, false, InputHealth.Inactive);
        if (Blocked(device, binding.Port) is { } why) return (why, true, false, InputHealth.Failing);
        var status = _serial.Status(device.Id);
        return status.State switch
        {
            SerialInputState.Listening => ($"Listening on {binding.Port} at {status.BaudRate} baud.", false, true, InputHealth.Listening),
            SerialInputState.Retrying => ($"{status.Problem} Trying again every few seconds.", true, false, InputHealth.Failing),
            _ => ($"Opening {binding.Port}…", false, false, InputHealth.Connecting),
        };
    }
}
