using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.App.Controls;
using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Inputs;
using GhostShow.Core.Model;
using GhostShow.Core.Storage;
using GhostShow.Dmx.Discovery;
using GhostShow.Inputs;

namespace GhostShow.App.ViewModels;

/// <summary>A port to choose for an input; Port null means "none".</summary>
public sealed record SerialPortChoice(string Label, string? Port)
{
    public override string ToString() => Label;
}

/// <summary>A serial port this computer has, in the list of what can be added: "COM5 · FTDI FT232R USB UART".</summary>
/// <param name="UsedBy">What already has it ("DMX universe 1", "Ethereal FX"); null if it's free to add.</param>
/// <param name="Hardware">The USB device on it, "FTDI FT232R USB UART", when the computer says.</param>
/// <param name="IsUsb">It's a USB device's port (the computer knows the device), not a built-in or unknown one.</param>
public sealed record FoundPort(string Port, string Label, string? UsedBy, string? Hardware = null, bool IsUsb = false)
{
    public bool CanAdd => UsedBy is null;
    public bool InUse => UsedBy is not null;
    public string Note => UsedBy is null ? "" : $"In use by {UsedBy}";

    /// <summary>The hardware's own name if the computer knows it, else the port: port names alone don't say which is which.</summary>
    public string Title => Hardware ?? Port;

    public string Summary => $"{(IsUsb ? "USB Serial" : "Serial Port")} · {Port}";

    public string Icon => IsUsb ? "Usb" : "SerialPort";

    public string Assignment => UsedBy is null ? "Available" : $"In use by {UsedBy}";
}

/// <summary>
/// The Inputs screen, laid out like the DMX outputs: on the left the show's input devices (each one's name and settings,
/// saved with the show, and the port it's on, saved on this computer), on the right what this computer has that could
/// be added, and below, the input monitor. A device is added from the port it's plugged into, named after the port until
/// renamed. Only ports picked for a device are opened, never probed; a port a DMX adapter has open isn't, and it says so.
/// The keyboard is always there and has no settings.
/// </summary>
public sealed partial class InputsViewModel : ObservableObject
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
    readonly ButtonsViewModel _buttons;
    readonly TimeProvider _time;
    IReadOnlyList<string> _ports = [];

    /// <param name="dmxPorts">The serial ports DMX adapters have open now, by universe. Not where an adapter was last
    /// seen: a port name moves to whatever device is plugged in first, so a missing adapter's old port may be the very
    /// device wanted here.</param>
    /// <param name="listPorts">The ports this computer has (tests pass their own).</param>
    /// <param name="describePort">The USB device on a port, or null if the computer won't say (tests pass their own).</param>
    /// <param name="findUsbSerial">The port the USB device with this serial number is on now, or null (tests pass their own).</param>
    public InputsViewModel(ShowSession session, SerialInputs serial, MachineSettings settings, Func<Task> saveSettings,
                           Func<IEnumerable<(int Universe, string Port)>> dmxPorts, ButtonsViewModel buttons,
                           Func<IReadOnlyList<string>>? listPorts = null, Func<string, UsbPortInfo?>? describePort = null,
                           Func<string, string?>? findUsbSerial = null, TimeProvider? time = null)
    {
        _session = session;
        _serial = serial;
        _settings = settings;
        _saveSettings = saveSettings;
        _dmxPorts = dmxPorts;
        _buttons = buttons;
        _listPorts = listPorts ?? SerialPortLink.PortNames;
        _describePort = describePort ?? UsbSerialPorts.Describe;
        _findUsbSerial = findUsbSerial ?? UsbSerialPorts.FindPortByUsbSerial;
        _time = time ?? TimeProvider.System;
        Monitor = new InputMonitorViewModel(buttons);
        Monitor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InputMonitorViewModel.ShowingOnly)) ShowWhichIsMonitored();
        };
        // Inputs added, renamed or removed here, undone, or changed on another device: the cards follow.
        session.Document.Changed += change =>
        {
            if (change.Touches("inputs")) Sync();
        };
        Reload();
    }

    /// <summary>The show's input devices, in order (the keyboard's card is fixed, in the view).</summary>
    public ObservableCollection<InputDeviceRowViewModel> Devices { get; } = new();

    /// <summary>The serial ports this computer has, to add as inputs.</summary>
    public ObservableCollection<FoundPort> Found { get; } = new();

    public InputMonitorViewModel Monitor { get; }

    public bool HasDevices => Devices.Count > 0;

    /// <summary>"No serial ports on this computer." when there are none to list.</summary>
    [ObservableProperty]
    public partial string FoundStatus { get; private set; } = "";

    /// <summary>A command from any device can be learned by a button waiting for its trigger.</summary>
    public bool IsListening => Devices.Any(d => d.IsListening);

    /// <summary>For the toolbar's warnings: each input that should be listening and isn't.</summary>
    public IEnumerable<string> Warnings => Devices.Where(d => d.IsFaulted).Select(d => $"{d.Name}: {d.StatusLabel} See Inputs.");

    // ---- the show's devices ----

    /// <summary>A new or opened show: its devices, put on this computer's old serial port if it's an older show's.</summary>
    public void Reload()
    {
        CarryOverOldSerialPort();
        Devices.Clear();
        Sync();
    }

    /// <summary>Matches the cards to the show's devices, keeping cards that stay, and listens to match.</summary>
    void Sync()
    {
        var model = _session.Project.Inputs;
        for (int i = 0; i < model.Count; i++)
        {
            if (i < Devices.Count && Devices[i].Model == model[i]) continue;
            int existing = -1;
            for (int j = i + 1; j < Devices.Count && existing < 0; j++)
                if (Devices[j].Model == model[i]) existing = j;
            if (existing >= 0) Devices.Move(existing, i);
            else Devices.Insert(i, new InputDeviceRowViewModel(model[i], this));
        }
        while (Devices.Count > model.Count) Devices.RemoveAt(Devices.Count - 1);
        foreach (var row in Devices)
        {
            row.Refresh();
            row.SetPorts(PortChoices(row));
        }
        OnPropertyChanged(nameof(HasDevices));
        RefreshFound();
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

    /// <summary>
    /// Adds a device on this port, named after the USB device on it, or the port if the computer won't say (rename it on
    /// its card), and listens to it.
    /// </summary>
    [RelayCommand]
    void Add(FoundPort found)
    {
        var device = new SerialInputDevice { Name = UniqueName(found.Hardware ?? found.Port) };
        SetBinding(device.Id, new InputPortBinding(device.Id, found.Port, _describePort(found.Port)?.Serial));
        _session.Document.Record("Add input", () => _session.Project.Inputs.Add(device), [ShowDocument.Shallow(_session.Project)]);
    }

    /// <summary>Removes the device from the show (undo puts it back, on the same port). Its triggers stay, saying it's gone.</summary>
    [RelayCommand]
    void Remove(InputDeviceRowViewModel row) =>
        _session.Document.Record("Remove input", () => _session.Project.Inputs.Remove(row.Model), [ShowDocument.Shallow(_session.Project)]);

    string UniqueName(string name)
    {
        string unique = name;
        for (int n = 2; _session.Project.Inputs.Any(i => string.Equals(i.Name, unique, StringComparison.OrdinalIgnoreCase)); n++) unique = $"{name} ({n})";
        return unique;
    }

    /// <summary>A device's settings changed (name, speed): one undo step per field, typing merged.</summary>
    internal void Edit(InputDevice device, string description, Action change, string field) =>
        _session.Document.Record(description, change, [ShowDocument.Shallow(device)], (device.Id, field));

    /// <summary>Shows this device's events only in the monitor below (or everything again).</summary>
    internal void ShowInMonitor(string deviceKey, string name) => Monitor.ShowOnly(deviceKey, name);

    void ShowWhichIsMonitored()
    {
        foreach (var row in Devices) row.IsMonitored = Monitor.ShowingOnly == row.DeviceKey;
        OnPropertyChanged(nameof(IsKeyboardMonitored));
    }

    public bool IsKeyboardMonitored => Monitor.ShowingOnly == InputMonitorRow.KeyOf(null);

    [RelayCommand]
    void MonitorKeyboard() => ShowInMonitor(InputMonitorRow.KeyOf(null), "Keyboard");

    // ---- this computer: which port each device is on ----

    internal InputPortBinding? BindingOf(Guid device) => _settings.InputPorts.FirstOrDefault(b => b.Input == device);

    /// <summary>Puts a device on a port (or none), on this computer, and listens to match.</summary>
    internal void SetBinding(Guid device, InputPortBinding? binding)
    {
        var old = BindingOf(device);
        if (old == binding) return;
        if (old is not null) _settings.InputPorts.Remove(old);
        if (binding is not null) _settings.InputPorts.Add(binding);
        _ = _saveSettings();
        foreach (var row in Devices) row.SetPorts(PortChoices(row));
        RefreshFound();
        Apply();
    }

    internal void PickPort(InputDeviceRowViewModel row, string? port)
    {
        var old = BindingOf(row.Model.Id);
        if (port is null) SetBinding(row.Model.Id, null);
        else if (old is null || !string.Equals(old.Port, port, PortComparison))
            SetBinding(row.Model.Id, new InputPortBinding(row.Model.Id, port, _describePort(port)?.Serial, old?.Enabled ?? true));
    }

    internal void SetEnabled(InputDeviceRowViewModel row, bool enabled)
    {
        if (BindingOf(row.Model.Id) is { } binding && binding.Enabled != enabled) SetBinding(row.Model.Id, binding with { Enabled = enabled });
    }

    /// <summary>Lists the ports again (after plugging in a USB-serial cable). A port picked stays, even if unplugged.</summary>
    [RelayCommand]
    void RefreshPorts()
    {
        _ports = _listPorts();
        foreach (var row in Devices) row.SetPorts(PortChoices(row));
        RefreshFound();
    }

    void RefreshFound()
    {
        var found = _ports.Select(p =>
        {
            var usb = _describePort(p);
            return new FoundPort(p, PortLabel(p), UsedBy(p, except: null), HardwareName(usb), usb is not null);
        }).ToList();
        if (!found.SequenceEqual(Found))
        {
            Found.Clear();
            foreach (var port in found) Found.Add(port);
        }
        FoundStatus = Found.Count == 0 ? "No serial ports on this computer. Plug in the device's USB cable and press Look again." : "";
    }

    List<SerialPortChoice> PortChoices(InputDeviceRowViewModel row)
    {
        var choices = new List<SerialPortChoice> { NoPort };
        foreach (var port in _ports)
            choices.Add(new SerialPortChoice(UsedBy(port, except: row.Model.Id) is { } user ? $"{PortLabel(port)} ({user})" : PortLabel(port), port));
        if (BindingOf(row.Model.Id) is { } binding && !_ports.Any(p => string.Equals(p, binding.Port, PortComparison)))
            choices.Add(new SerialPortChoice($"{binding.Port} (not connected)", binding.Port));
        return choices;
    }

    /// <summary>
    /// "COM5 · FTDI FT232R USB UART": the USB device's maker and product name, when the computer can say. Port names alone
    /// don't say which device is which.
    /// </summary>
    string PortLabel(string port) => HardwareName(_describePort(port)) is { } name ? $"{port} · {name}" : port;

    /// <summary>"FTDI FT232R USB UART": the USB device's maker and product, or null if the computer won't say.</summary>
    static string? HardwareName(UsbPortInfo? usb)
    {
        if (usb is null) return null;
        var name = string.Join(" ", new[] { usb.Manufacturer, usb.Product }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return name.Length > 0 ? name : null;
    }

    /// <summary>What a device's card says about the hardware on its port: its name, and whether it's USB.</summary>
    internal (string? Name, bool IsUsb) HardwareOn(string port) => _describePort(port) is { } usb ? (HardwareName(usb), true) : (null, false);

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
        Refresh();
    }

    /// <summary>Shows how each device stands. The owner calls it, on the UI thread, when a port's status changes.</summary>
    public void Refresh()
    {
        foreach (var row in Devices) row.ShowStatus(Status(row.Model));
        OnPropertyChanged(nameof(Warnings));
        _buttons.IsSerialListening = IsListening;
    }

    /// <summary>
    /// How a device stands, in words and for its status light. In the show but on no port here is grey rather than red:
    /// nothing is wrong with any hardware, a port just needs picking (it's still among the toolbar's warnings).
    /// </summary>
    (string Label, bool Faulted, bool Listening, DeviceState State) Status(InputDevice device)
    {
        if (BindingOf(device.Id) is not { } binding) return ("Not on a port on this computer: pick the port it's plugged into.", true, false, DeviceState.Inactive);
        if (!binding.Enabled) return ($"Off: {binding.Port} isn't being listened to.", false, false, DeviceState.Inactive);
        if (Blocked(device, binding.Port) is { } why) return (why, true, false, DeviceState.Error);
        var status = _serial.Status(device.Id);
        return status.State switch
        {
            SerialInputState.Listening => ($"Listening on {binding.Port} at {status.BaudRate} baud.", false, true, DeviceState.Healthy),
            SerialInputState.Retrying => ($"{status.Problem} Trying again every few seconds.", true, false, DeviceState.Error),
            _ => ($"Opening {binding.Port}…", false, false, DeviceState.Connecting),
        };
    }

    /// <summary>A device sent a command: shown on its card, then pressed like any input. On the UI thread.</summary>
    public void OnCommand(Guid device, string command)
    {
        Devices.FirstOrDefault(d => d.Model.Id == device)?.Received(command, _time.GetLocalNow());
        _buttons.Command(device, command);
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
        if (apply)
        {
            foreach (var row in Devices) row.SetPorts(PortChoices(row));
            Apply();
        }
        foreach (var row in Devices) row.Tick(_time.GetLocalNow());
    }
}

/// <summary>One of the show's input devices, on the Inputs screen: its name and speed (the show's), its port and whether it's listened to (this computer's), and how it stands.</summary>
public sealed partial class InputDeviceRowViewModel : ObservableObject
{
    readonly InputsViewModel _owner;
    bool _settingPorts;

    public InputDeviceRowViewModel(InputDevice model, InputsViewModel owner)
    {
        Model = model;
        _owner = owner;
    }

    public InputDevice Model { get; }

    public string DeviceKey => InputMonitorRow.KeyOf(Model.Id);

    /// <summary>"USB Serial Input": what it is, as far as this computer can tell from its port.</summary>
    public string KindLabel => Model switch
    {
        SerialInputDevice => _hardware.IsUsb ? "USB Serial Input" : "Serial Input",
        _ => "Input",
    };

    /// <summary>"USB Serial Input · COM4 · 9600 baud": what it is and where, under its name.</summary>
    public string Summary => Model is SerialInputDevice serial
        ? $"{KindLabel} · {Port ?? "No port on this computer"} · {serial.BaudRate} baud"
        : KindLabel;

    public string Icon => _hardware.IsUsb ? "Usb" : "SerialPort";

    /// <summary>The port it's on, on this computer, or null.</summary>
    public string? Port => _owner.BindingOf(Model.Id)?.Port;

    /// <summary>The USB device on its port, "FTDI FT232R USB UART", if the computer says.</summary>
    public string HardwareLabel => _hardware.Name ?? (Port is null ? "—" : "Not reported by this computer");

    /// <summary>The USB serial number it was found by, which lets it be followed to another port if it moves.</summary>
    public string UsbSerialLabel => _owner.BindingOf(Model.Id)?.UsbSerial ?? "—";

    /// <summary>The framing every serial input uses (see SerialLink): only the speed is the device's own.</summary>
    public static string SerialFormat => "8 data bits · no parity · 1 stop bit";

    (string? Name, bool IsUsb) _hardware;
    bool _shownOnce;

    /// <summary>Its settings are showing. They open by themselves for a device with no port on this computer, to pick one.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public string Name
    {
        get => Model.Name;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == Model.Name) return;
            _owner.Edit(Model, "Rename input", () => Model.Name = value.Trim(), "name");
        }
    }

    public bool IsSerial => Model is SerialInputDevice;

    public int BaudRate
    {
        get => (Model as SerialInputDevice)?.BaudRate ?? 0;
        set
        {
            if (Model is not SerialInputDevice serial || value <= 0 || value == serial.BaudRate) return;
            _owner.Edit(Model, "Change input speed", () => serial.BaudRate = value, "baud");
        }
    }

    /// <summary>After a change to the show: every field shows the model.</summary>
    internal void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(BaudRate));
        OnPropertyChanged(nameof(Summary));
    }

    // ---- this computer ----

    public ObservableCollection<SerialPortChoice> Ports { get; } = new();

    [ObservableProperty]
    public partial SerialPortChoice? SelectedPort { get; set; }

    partial void OnSelectedPortChanged(SerialPortChoice? value)
    {
        if (!_settingPorts && value is not null) _owner.PickPort(this, value.Port);
    }

    /// <summary>Off keeps the port picked but closed, e.g. to let another program use it.</summary>
    public bool IsEnabled
    {
        get => _owner.BindingOf(Model.Id)?.Enabled ?? true;
        set => _owner.SetEnabled(this, value);
    }

    public bool HasPort => _owner.BindingOf(Model.Id) is not null;

    internal void SetPorts(IReadOnlyList<SerialPortChoice> choices)
    {
        _settingPorts = true;
        try
        {
            if (!choices.SequenceEqual(Ports))
            {
                Ports.Clear();
                foreach (var choice in choices) Ports.Add(choice);
            }
            string? port = _owner.BindingOf(Model.Id)?.Port;
            SelectedPort = Ports.FirstOrDefault(p => p.Port == port) ?? Ports.FirstOrDefault();
        }
        finally
        {
            _settingPorts = false;
        }
        if (!_shownOnce)
        {
            _shownOnce = true;
            IsExpanded = Port is null && IsSerial;
        }
        var hardware = Port is { } boundPort ? _owner.HardwareOn(boundPort) : (null, false);
        if (hardware != _hardware)
        {
            _hardware = hardware;
            OnPropertyChanged(nameof(KindLabel));
            OnPropertyChanged(nameof(Icon));
            OnPropertyChanged(nameof(HardwareLabel));
        }
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(HasPort));
        OnPropertyChanged(nameof(Port));
        OnPropertyChanged(nameof(UsbSerialLabel));
        OnPropertyChanged(nameof(Summary));
    }

    // ---- how it stands ----

    /// <summary>"Listening on COM5 at 57600 baud", "COM5 isn't connected. Trying again every few seconds."</summary>
    [ObservableProperty]
    public partial string StatusLabel { get; private set; } = "";

    /// <summary>It should be listening and isn't (no port, missing, in use, a DMX adapter's).</summary>
    [ObservableProperty]
    public partial bool IsFaulted { get; private set; }

    [ObservableProperty]
    public partial bool IsListening { get; private set; }

    /// <summary>The status light: listening (green), opening (amber), failing (red), or off / on no port here (grey).</summary>
    [ObservableProperty]
    public partial DeviceState State { get; private set; } = DeviceState.Inactive;

    internal void ShowStatus((string Label, bool Faulted, bool Listening, DeviceState State) status) =>
        (StatusLabel, IsFaulted, IsListening, State) = status;

    /// <summary>"Last: “BTN24” 4 s ago", so it's clear at a glance whether the device is talking at all.</summary>
    [ObservableProperty]
    public partial string LastReceived { get; private set; } = "Nothing received yet";

    string? _lastCommand;
    DateTimeOffset _lastAt;

    internal void Received(string command, DateTimeOffset at)
    {
        (_lastCommand, _lastAt) = (command, at);
        Tick(at);
    }

    internal void Tick(DateTimeOffset now)
    {
        if (_lastCommand is null) return;
        var ago = now - _lastAt;
        string when = ago.TotalSeconds < 1 ? "just now"
            : ago.TotalMinutes < 1 ? $"{ago.TotalSeconds:0} s ago"
            : ago.TotalHours < 1 ? $"{ago.TotalMinutes:0} min ago"
            : $"at {_lastAt:HH:mm}";
        LastReceived = $"Last: “{InputText.Visible(_lastCommand)}” {when}";
    }

    /// <summary>The monitor below shows only this device's events.</summary>
    [ObservableProperty]
    public partial bool IsMonitored { get; set; }

    [RelayCommand]
    void Monitor() => _owner.ShowInMonitor(DeviceKey, Model.Name);
}
