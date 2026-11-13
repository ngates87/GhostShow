using GhostShow.App.ViewModels;
using GhostShow.Core.Editing;
using GhostShow.Core.Inputs;
using GhostShow.Core.Model;
using GhostShow.Core.Storage;
using GhostShow.Dmx.Discovery;
using GhostShow.Inputs;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhostShow.App.Tests;

/// <summary>
/// The Inputs screen: adding a device from the port it's on, its settings (the show's) and port (this computer's),
/// never taking a DMX adapter's port, following a USB device to a new port, and carrying an older show's serial port over.
/// </summary>
public class InputsTests : IDisposable
{
    /// <summary>A port that opens and stays quiet.</summary>
    sealed class QuietLink : ISerialLink
    {
        public int Read(byte[] buffer)
        {
            Thread.Sleep(10);
            return 0;
        }

        public void Dispose() { }
    }

    readonly TimelineRig _rig = new();
    readonly MachineSettings _settings = new();
    readonly List<(int Universe, string Port)> _dmx = [];
    readonly List<string> _opened = [];
    readonly HashSet<string> _unplugged = [];
    readonly Dictionary<string, string> _usbSerials = new() { ["COM4"] = "A1B2" };
    readonly SerialInputs _serial;
    readonly ButtonsViewModel _buttons;
    int _saves;

    public InputsTests()
    {
        _serial = new SerialInputs(NullLogger.Instance, (port, baud) =>
        {
            lock (_unplugged)
                if (_unplugged.Contains(port)) throw new FileNotFoundException();
            lock (_opened) _opened.Add($"{port} {baud}");
            return new QuietLink();
        }, TimeSpan.FromMilliseconds(20));
        _buttons = new ButtonsViewModel(_rig.Session, _rig.Playback, () => { }, () => { });
    }

    public void Dispose()
    {
        _serial.Dispose();
        _rig.Dispose();
    }

    Project Show => _rig.Session.Project;

    InputsViewModel Screen(params string[] ports)
    {
        var screen = new InputsViewModel(_rig.Session, _serial, _settings, () => { _saves++; return Task.CompletedTask; }, () => _dmx, _buttons,
            () => ports,
            port => port == "COM4" ? new UsbPortInfo(0x0403, 0x6001, "Ethereal", "FX QC-24", "A1B2") : null,
            usb => _usbSerials.FirstOrDefault(p => p.Value == usb).Key);
        screen.RefreshPortsCommand.Execute(null);
        return screen;
    }

    /// <summary>Waits for a device's port to reach this state (it opens in the background), then shows it, as the UI thread would.</summary>
    void Settle(InputsViewModel screen, Guid device, SerialInputState expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_serial.Status(device).State != expected && DateTime.UtcNow < deadline) Thread.Sleep(5);
        screen.Refresh();
    }

    [Fact]
    public void Adding_a_port_adds_a_device_named_after_it_that_listens_on_it()
    {
        var screen = Screen("COM3", "COM4");
        Assert.Equal(["COM3", "COM4 · Ethereal FX QC-24"], screen.Found.Select(f => f.Label));   // the USB device's own name, where the computer says
        Assert.All(screen.Found, f => Assert.True(f.CanAdd));

        screen.AddCommand.Execute(screen.Found[1]);
        var device = Assert.IsType<SerialInputDevice>(Assert.Single(Show.Inputs));
        Assert.Equal(("COM4", 9600), (device.Name, device.BaudRate));
        Assert.Equal(new InputPortBinding(device.Id, "COM4", "A1B2"), Assert.Single(_settings.InputPorts));   // the USB serial number, to follow it
        var row = Assert.Single(screen.Devices);
        Settle(screen, device.Id, SerialInputState.Listening);
        Assert.Equal("Listening on COM4 at 9600 baud.", row.StatusLabel);
        Assert.True(screen.IsListening);
        Assert.True(_buttons.IsSerialListening);   // a button waiting for its trigger takes a command too
        Assert.Empty(screen.Warnings);
        Assert.Equal("COM4", screen.Found[1].UsedBy);

        // Its name and speed are the show's: edits, with undo.
        row.Name = "Ethereal FX";
        Assert.Equal("Ethereal FX", device.Name);
        Assert.Equal("In use by Ethereal FX", screen.Found[1].Note);
        row.BaudRate = 57600;
        Settle(screen, device.Id, SerialInputState.Listening);
        Assert.Equal("Listening on COM4 at 57600 baud.", row.StatusLabel);

        // Listening is this computer's: off keeps the port picked but closed.
        row.IsEnabled = false;
        Assert.Equal(SerialInputState.Off, _serial.Status(device.Id).State);
        Assert.Equal("Off: COM4 isn't being listened to.", row.StatusLabel);
        Assert.False(screen.IsListening);
        lock (_opened) Assert.Equal(["COM4 9600", "COM4 57600"], _opened);
        Assert.True(_saves >= 2);
    }

    [Fact]
    public void Removing_a_device_stops_listening_and_undo_puts_it_back_on_its_port()
    {
        var screen = Screen("COM3");
        screen.AddCommand.Execute(screen.Found[0]);
        var device = Show.Inputs.Single();
        Settle(screen, device.Id, SerialInputState.Listening);

        screen.RemoveCommand.Execute(screen.Devices.Single());
        Assert.Empty(Show.Inputs);
        Assert.Empty(screen.Devices);
        Assert.Equal(SerialInputState.Off, _serial.Status(device.Id).State);
        Assert.True(screen.Found[0].CanAdd);

        _rig.Session.Document.UndoLatest(null);
        Assert.Same(device, Show.Inputs.Single());
        Settle(screen, device.Id, SerialInputState.Listening);
        Assert.Equal("Listening on COM3 at 9600 baud.", screen.Devices.Single().StatusLabel);
    }

    [Fact]
    public void A_port_a_DMX_adapter_is_on_is_not_opened_and_says_so()
    {
        _dmx.Add((1, "COM3"));
        var screen = Screen("COM3", "COM4");
        Assert.Equal("DMX universe 1", screen.Found[0].UsedBy);
        Assert.False(screen.Found[0].CanAdd);

        // A device on COM3, from a show set up while the adapter was elsewhere.
        var device = new SerialInputDevice { Name = "Ethereal FX" };
        _settings.InputPorts.Add(new InputPortBinding(device.Id, "COM3"));
        _rig.Session.Document.Record("Add input", () => Show.Inputs.Add(device), [ShowDocument.Shallow(Show)]);
        var row = screen.Devices.Single();
        Assert.Equal(SerialInputState.Off, _serial.Status(device.Id).State);
        Assert.Equal("COM3 is DMX universe 1's adapter: pick another port.", row.StatusLabel);
        Assert.True(row.IsFaulted);
        Assert.Equal(["Ethereal FX: COM3 is DMX universe 1's adapter: pick another port. See Inputs."], screen.Warnings);
        Assert.Contains(row.Ports, p => p.Label == "COM3 (DMX universe 1)");

        // The DMX adapter moves to another port: the input takes COM3 on the next tick.
        _dmx[0] = (1, "COM4");
        screen.Tick();
        Settle(screen, device.Id, SerialInputState.Listening);
        Assert.Equal("Listening on COM3 at 9600 baud.", row.StatusLabel);
        lock (_opened) Assert.Equal(["COM3 9600"], _opened);
    }

    [Fact]
    public void A_device_with_no_port_on_this_computer_says_so_until_one_is_picked()
    {
        // A show set up on another computer: the device is the show's, its port that computer's.
        var device = new SerialInputDevice { Name = "Ethereal FX", BaudRate = 57600 };
        Show.Inputs.Add(device);
        var screen = Screen("COM3", "COM4");
        var row = screen.Devices.Single();
        Assert.Equal("Not on a port on this computer: pick the port it's plugged into.", row.StatusLabel);
        Assert.True(row.IsFaulted);
        Assert.Equal("None", row.SelectedPort?.Label);

        row.SelectedPort = row.Ports.Single(p => p.Port == "COM4");
        Settle(screen, device.Id, SerialInputState.Listening);
        Assert.Equal("Listening on COM4 at 57600 baud.", row.StatusLabel);
        Assert.Equal(new InputPortBinding(device.Id, "COM4", "A1B2"), _settings.InputPorts.Single());
    }

    [Fact]
    public void A_USB_device_that_comes_back_on_another_port_is_followed()
    {
        var device = new SerialInputDevice { Name = "Ethereal FX" };
        Show.Inputs.Add(device);
        _settings.InputPorts.Add(new InputPortBinding(device.Id, "COM4", "A1B2"));
        lock (_unplugged) _unplugged.Add("COM4");
        var screen = Screen("COM3", "COM6");
        Settle(screen, device.Id, SerialInputState.Retrying);
        Assert.Equal("COM4 isn't connected. Trying again every few seconds.", screen.Devices.Single().StatusLabel);

        _usbSerials.Clear();
        _usbSerials["COM6"] = "A1B2";   // plugged into another socket: Windows gives it another name
        screen.Tick();
        Settle(screen, device.Id, SerialInputState.Listening);
        Assert.Equal("Listening on COM6 at 9600 baud.", screen.Devices.Single().StatusLabel);
        Assert.Equal("COM6", _settings.InputPorts.Single().Port);
    }

    [Fact]
    public void An_older_show_s_serial_device_goes_on_the_port_this_computer_listened_on()
    {
        _settings.SerialInput = new SerialInputSettings("COM7", 4800);
        Show.Buttons.Add(new ShowButton { Name = "Scare", Triggers = [new ButtonTrigger { Source = ButtonTrigger.Serial, Address = "GO" }] });
        LegacySerialTriggers.Apply(Show);   // as loading an older show does

        var screen = Screen("COM3");
        var device = Assert.IsType<SerialInputDevice>(Show.Inputs.Single());
        Assert.Equal((LegacySerialTriggers.DeviceName, 4800), (device.Name, device.BaudRate));
        Assert.Equal(device.Id, Show.Buttons[0].Triggers[0].Input);
        Assert.Equal(new InputPortBinding(device.Id, "COM7"), _settings.InputPorts.Single());
        Assert.Null(_settings.SerialInput);
        Assert.Equal("COM7 (not connected)", screen.Devices.Single().SelectedPort?.Label);
    }

    [Fact]
    public void A_command_shows_on_its_device_s_card_and_its_monitor_button_shows_only_its_events()
    {
        var screen = Screen("COM3");
        screen.AddCommand.Execute(screen.Found[0]);
        var row = screen.Devices.Single();
        row.Name = "Ethereal FX";
        Assert.Equal("Nothing received yet", row.LastReceived);

        screen.OnCommand(row.Model.Id, "BTN24");
        _buttons.PressKey("F5");
        Assert.StartsWith("Last: “BTN24” ", row.LastReceived);
        Assert.Equal([("Keyboard", "F5"), ("Ethereal FX", "BTN24")], screen.Monitor.Rows.Select(r => (r.Source, r.Address)));

        row.MonitorCommand.Execute(null);
        Assert.True(row.IsMonitored);
        Assert.Equal("Showing Ethereal FX only", screen.Monitor.FilterLabel);
        Assert.Equal(["BTN24"], screen.Monitor.Rows.Select(r => r.Address));
        _buttons.PressKey("F6");
        Assert.Single(screen.Monitor.Rows);   // still only its own

        screen.MonitorKeyboardCommand.Execute(null);
        Assert.False(row.IsMonitored);
        Assert.True(screen.IsKeyboardMonitored);
        Assert.Equal(["F6", "F5"], screen.Monitor.Rows.Select(r => r.Address));

        screen.Monitor.ShowAllCommand.Execute(null);
        Assert.False(screen.IsKeyboardMonitored);
        Assert.Equal(3, screen.Monitor.Rows.Count);
    }
}
