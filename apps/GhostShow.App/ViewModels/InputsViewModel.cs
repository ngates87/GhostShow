using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.App.Controls;
using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Inputs;
using GhostShow.Core.Model;

namespace GhostShow.App.ViewModels;

/// <summary>
/// The Inputs screen, laid out like the DMX outputs: on the left the show's input devices (each one's name and settings,
/// saved with the show, and the port it's on, saved on the show computer), on the right what the show computer has that
/// could be added, and below, the input monitor. A device is added from the port it's plugged into, named after the port
/// until renamed. The ports are the show computer's (<see cref="IInputSetup"/>), on its screen and a device's alike. The
/// keyboard is always there and has no settings.
/// </summary>
public sealed partial class InputsViewModel : ObservableObject
{
    readonly ShowSession _session;
    readonly IInputSetup _setup;
    readonly ButtonsViewModel _buttons;
    readonly TimeProvider _time;

    public InputsViewModel(ShowSession session, IInputSetup setup, ButtonsViewModel buttons, TimeProvider? time = null)
    {
        _session = session;
        _setup = setup;
        _buttons = buttons;
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
        setup.Changed += ShowSetup;
        Reload();
    }

    /// <summary>The show's input devices, in order (the keyboard's card is fixed, in the view).</summary>
    public ObservableCollection<InputDeviceRowViewModel> Devices { get; } = new();

    /// <summary>The serial ports the show computer has, to add as inputs.</summary>
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

    /// <summary>A new or opened show: its devices.</summary>
    public void Reload()
    {
        Devices.Clear();
        Sync();
    }

    /// <summary>Matches the cards to the show's devices, keeping cards that stay.</summary>
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
        foreach (var row in Devices) row.Refresh();
        OnPropertyChanged(nameof(HasDevices));
        ShowSetup();
    }

    /// <summary>The show computer's ports: what can be added, and where each device is and how it stands.</summary>
    void ShowSetup()
    {
        var state = _setup.State;
        if (!state.Found.SequenceEqual(Found))
        {
            Found.Clear();
            foreach (var port in state.Found) Found.Add(port);
        }
        FoundStatus = state.FoundStatus;
        var now = _time.GetLocalNow();
        foreach (var row in Devices) row.Show(state.Of(row.Model.Id), now);
        OnPropertyChanged(nameof(Warnings));
        OnPropertyChanged(nameof(IsListening));
        _buttons.IsSerialListening = IsListening;
    }

    /// <summary>
    /// Adds a device on this port, named after the USB device on it, or the port if the computer won't say (rename it on
    /// its card), and listens to it.
    /// </summary>
    [RelayCommand]
    void Add(FoundPort found)
    {
        var device = new SerialInputDevice { Name = UniqueName(found.Hardware ?? found.Port) };
        _setup.PickPort(device.Id, found.Port);   // first, so it's listened to as soon as it's in the show
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

    // ---- the show computer: which port each device is on ----

    internal void PickPort(InputDeviceRowViewModel row, string? port) => _setup.PickPort(row.Model.Id, port);

    internal void SetEnabled(InputDeviceRowViewModel row, bool enabled) => _setup.SetEnabled(row.Model.Id, enabled);

    /// <summary>Lists the ports again (after plugging in a USB-serial cable). A port picked stays, even if unplugged.</summary>
    [RelayCommand]
    void RefreshPorts() => _setup.RefreshPorts();

    /// <summary>From the screens' refresh: how long ago each device last sent something.</summary>
    public void Tick()
    {
        var now = _time.GetLocalNow();
        foreach (var row in Devices) row.Tick(now);
    }
}

/// <summary>One of the show's input devices, on the Inputs screen: its name and speed (the show's), its port and whether it's listened to (the show computer's), and how it stands.</summary>
public sealed partial class InputDeviceRowViewModel : ObservableObject
{
    readonly InputsViewModel _owner;
    bool _settingPorts;
    InputDeviceSetup? _setup;

    public InputDeviceRowViewModel(InputDevice model, InputsViewModel owner)
    {
        Model = model;
        _owner = owner;
    }

    public InputDevice Model { get; }

    public string DeviceKey => InputMonitorRow.KeyOf(Model.Id);

    bool IsUsb => _setup?.IsUsb == true;

    /// <summary>"USB Serial Input": what it is, as far as the show computer can tell from its port.</summary>
    public string KindLabel => Model switch
    {
        SerialInputDevice => IsUsb ? "USB Serial Input" : "Serial Input",
        _ => "Input",
    };

    /// <summary>"USB Serial Input · COM4 · 9600 baud": what it is and where, under its name.</summary>
    public string Summary => Model is SerialInputDevice serial
        ? $"{KindLabel} · {Port ?? "No port on this computer"} · {serial.BaudRate} baud"
        : KindLabel;

    public string Icon => IsUsb ? "Usb" : "SerialPort";

    /// <summary>The port it's on, on the show computer, or null.</summary>
    public string? Port => _setup?.Port;

    /// <summary>The USB device on its port, "FTDI FT232R USB UART", if the computer says.</summary>
    public string HardwareLabel => _setup?.Hardware ?? (Port is null ? "—" : "Not reported by this computer");

    /// <summary>The USB serial number it was found by, which lets it be followed to another port if it moves.</summary>
    public string UsbSerialLabel => _setup?.UsbSerial ?? "—";

    /// <summary>The framing every serial input uses (see SerialLink): only the speed is the device's own.</summary>
    public static string SerialFormat => "8 data bits · no parity · 1 stop bit";

    bool _shownOnce;

    /// <summary>Its settings are showing. They open by themselves for a device with no port on the show computer, to pick one.</summary>
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

    // ---- the show computer ----

    public ObservableCollection<SerialPortChoice> Ports { get; } = new();

    [ObservableProperty]
    public partial SerialPortChoice? SelectedPort { get; set; }

    partial void OnSelectedPortChanged(SerialPortChoice? value)
    {
        if (!_settingPorts && value is not null && value.Port != Port) _owner.PickPort(this, value.Port);
    }

    /// <summary>Off keeps the port picked but closed, e.g. to let another program use it.</summary>
    public bool IsEnabled
    {
        get => _setup?.Enabled ?? true;
        set => _owner.SetEnabled(this, value);
    }

    public bool HasPort => Port is not null;

    /// <summary>Where it is on the show computer and how it stands (null: not heard yet, on a device).</summary>
    internal void Show(InputDeviceSetup? setup, DateTimeOffset now)
    {
        _setup = setup;
        _settingPorts = true;
        try
        {
            var choices = setup?.Ports ?? [];
            if (!choices.SequenceEqual(Ports))
            {
                Ports.Clear();
                foreach (var choice in choices) Ports.Add(choice);
            }
            SelectedPort = Ports.FirstOrDefault(p => p.Port == Port) ?? Ports.FirstOrDefault();
        }
        finally
        {
            _settingPorts = false;
        }
        if (!_shownOnce && setup is not null)
        {
            _shownOnce = true;
            IsExpanded = Port is null && IsSerial;
        }
        foreach (var property in (string[])[nameof(KindLabel), nameof(Icon), nameof(HardwareLabel), nameof(IsEnabled), nameof(HasPort), nameof(Port),
                     nameof(UsbSerialLabel), nameof(Summary)])
            OnPropertyChanged(property);
        StatusLabel = setup?.Status ?? "";
        IsFaulted = setup?.IsFaulted == true;
        IsListening = setup?.IsListening == true;
        State = setup?.Health switch
        {
            InputHealth.Listening => DeviceState.Healthy,
            InputHealth.Connecting => DeviceState.Connecting,
            InputHealth.Failing => DeviceState.Error,
            _ => DeviceState.Inactive,
        };
        Tick(now);
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

    /// <summary>"Last: “BTN24” 4 s ago", so it's clear at a glance whether the device is talking at all.</summary>
    [ObservableProperty]
    public partial string LastReceived { get; private set; } = "Nothing received yet";

    internal void Tick(DateTimeOffset now)
    {
        if (_setup is not { LastCommand: { } command, LastAt: { } at }) return;
        var ago = now - at;
        string when = ago.TotalSeconds < 1 ? "just now"
            : ago.TotalMinutes < 1 ? $"{ago.TotalSeconds:0} s ago"
            : ago.TotalHours < 1 ? $"{ago.TotalMinutes:0} min ago"
            : $"at {at.ToLocalTime():HH:mm}";
        LastReceived = $"Last: “{InputText.Visible(command)}” {when}";
    }

    /// <summary>The monitor below shows only this device's events.</summary>
    [ObservableProperty]
    public partial bool IsMonitored { get; set; }

    [RelayCommand]
    void Monitor() => _owner.ShowInMonitor(DeviceKey, Model.Name);
}
