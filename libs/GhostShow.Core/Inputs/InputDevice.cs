using System.Text.Json.Serialization;

namespace GhostShow.Core.Inputs;

/// <summary>
/// A device the show listens to (Inputs screen), like a universe is one it sends to: a sensor box, and later a Stream
/// Deck, GPIO pins, OSC or DMX in. The show stores its name, its kind and how to talk to it ("Ethereal FX", serial at
/// 57600 baud); which port or unit it is on belongs to each computer (<see cref="Storage.InputPortBinding"/>), so a show
/// opened elsewhere keeps its triggers and only needs the port picked. A button's trigger names the device it listens
/// to, and what to listen for on it (a command, a pin, a key). The keyboard isn't one: it's always there.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SerialInputDevice), "serial")]
public abstract class InputDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What the show calls it: the port or device it was added from ("COM5", "Ethereal FX QC-24") until renamed.</summary>
    public string Name { get; set; } = "Input";
}

/// <summary>
/// A device on a serial port that sends commands, one per line (a sensor box or prop controller). Always 8 data bits, no
/// parity, 1 stop bit. Like VenueMagic, a command is a whole line ending in CR or LF.
/// </summary>
public sealed class SerialInputDevice : InputDevice
{
    /// <summary>The usual speeds; 9600 is the most common for sensor boxes.</summary>
    public static IReadOnlyList<int> BaudRates { get; } = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];

    /// <summary>Its speed, which the device's manual gives. The device's, not the computer's, so it's saved with the show.</summary>
    public int BaudRate { get; set; } = 9600;
}
