using System.Text.Json.Serialization;

namespace GhostShow.Core.Inputs;

/// <summary>A port to choose for an input; Port null means "none".</summary>
public sealed record SerialPortChoice(string Label, string? Port)
{
    public override string ToString() => Label;
}

/// <summary>A serial port the show computer has, in the list of what can be added: "COM5 · FTDI FT232R USB UART".</summary>
/// <param name="UsedBy">What already has it ("DMX universe 1", "Ethereal FX"); null if it's free to add.</param>
/// <param name="Hardware">The USB device on it, "FTDI FT232R USB UART", when the computer says.</param>
/// <param name="IsUsb">It's a USB device's port (the computer knows the device), not a built-in or unknown one.</param>
public sealed record FoundPort(string Port, string Label, string? UsedBy, string? Hardware = null, bool IsUsb = false)
{
    [JsonIgnore]
    public bool CanAdd => UsedBy is null;
    [JsonIgnore]
    public bool InUse => UsedBy is not null;
    [JsonIgnore]
    public string Note => UsedBy is null ? "" : $"In use by {UsedBy}";

    /// <summary>The hardware's own name if the computer knows it, else the port: port names alone don't say which is which.</summary>
    [JsonIgnore]
    public string Title => Hardware ?? Port;

    [JsonIgnore]
    public string Summary => $"{(IsUsb ? "USB Serial" : "Serial Port")} · {Port}";

    [JsonIgnore]
    public string Icon => IsUsb ? "Usb" : "SerialPort";

    [JsonIgnore]
    public string Assignment => UsedBy is null ? "Available" : $"In use by {UsedBy}";
}

/// <summary>How an input device is doing, for its status light.</summary>
public enum InputHealth
{
    /// <summary>Off, or on no port on the show computer.</summary>
    Inactive,
    Listening,
    /// <summary>Opening its port.</summary>
    Connecting,
    /// <summary>Should be listening and isn't (unplugged, in use, failed).</summary>
    Failing,
}

/// <summary>One of the show's input devices on the show computer: the port it's on there, and how it stands.</summary>
/// <param name="Port">The port it's on, or null.</param>
/// <param name="UsbSerial">The USB serial number it was found by, which lets it be followed to another port.</param>
/// <param name="Hardware">The USB device on its port, "FTDI FT232R USB UART", if the computer says.</param>
/// <param name="Ports">The ports it could be put on, "None" first.</param>
/// <param name="Status">"Listening on COM5 at 57600 baud", "COM5 isn't connected. Trying again every few seconds."</param>
/// <param name="IsFaulted">It should be listening and isn't (also when it's on no port: a port needs picking).</param>
/// <param name="LastCommand">The last command it sent, and when (<paramref name="LastAt"/>), or null.</param>
public sealed record InputDeviceSetup(Guid Device, string? Port, string? UsbSerial, bool Enabled, string? Hardware, bool IsUsb,
                                      IReadOnlyList<SerialPortChoice> Ports, string Status, bool IsFaulted, bool IsListening,
                                      InputHealth Health, string? LastCommand = null, DateTimeOffset? LastAt = null)
{
    public bool SameAs(InputDeviceSetup other) => this with { Ports = other.Ports } == other && Ports.SequenceEqual(other.Ports);
}

/// <summary>Everything the Inputs screen shows about the show computer's serial ports.</summary>
/// <param name="FoundStatus">"No serial ports on this computer…" when there are none to list.</param>
public sealed record InputSetupState(string FoundStatus, IReadOnlyList<FoundPort> Found, IReadOnlyList<InputDeviceSetup> Devices)
{
    public static readonly InputSetupState Empty = new("", [], []);

    /// <summary>The same on screen? (A record's own comparison would only compare the lists by reference.)</summary>
    public bool SameAs(InputSetupState other) =>
        FoundStatus == other.FoundStatus && Found.SequenceEqual(other.Found)
        && Devices.Count == other.Devices.Count && Devices.Zip(other.Devices).All(pair => pair.First.SameAs(pair.Second));

    public InputDeviceSetup? Of(Guid device) => Devices.FirstOrDefault(d => d.Device == device);
}

/// <summary>
/// The show computer's serial ports, for the Inputs screen: which ports it has, which one each of the show's input
/// devices is on there (saved in its settings, not the show), and how each is listened to. The ports only exist on the
/// show computer (<c>LocalInputSetup</c>); a device's screen asks over the connection (<c>RemoteInputSetup</c> in
/// GhostShow.Browser).
/// </summary>
public interface IInputSetup
{
    InputSetupState State { get; }

    /// <summary><see cref="State"/> changed. On the show's thread on the show computer; on the UI thread on a device.</summary>
    event Action? Changed;

    /// <summary>Lists the ports again (after plugging in a USB-serial cable). A port picked stays, even if unplugged.</summary>
    void RefreshPorts();

    /// <summary>Puts a device on a port (null: none) and listens to it there. Returns why it couldn't, or null.</summary>
    string? PickPort(Guid device, string? port);

    /// <summary>Off keeps the port picked but closed, e.g. to let another program use it.</summary>
    string? SetEnabled(Guid device, bool enabled);
}
