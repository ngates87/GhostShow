using System.Text.Json.Serialization;

namespace GhostShow.Core.Storage;

/// <summary>What a paired device may do (see docs/plan-remote.md).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RemoteRole>))]
public enum RemoteRole
{
    /// <summary>Everything: run the show, live faders, edit.</summary>
    Editor,

    /// <summary>Run the show and see its state (start and stop, buttons, Blackout, stats); no edits, no live faders.</summary>
    Operator,
}

/// <summary>
/// The web remote on this computer: whether devices on the network may connect, on which port, and the devices
/// paired with it. A device pairs once with the PIN shown on the desktop and then keeps a token; only a hash of the
/// token is kept here.
/// </summary>
public sealed class RemoteSettings
{
    public const int DefaultPort = 7700;

    /// <summary>Off until it's switched on: nothing listens on the network unless asked to.</summary>
    public bool Enabled { get; set; }

    public int Port { get; set; } = DefaultPort;

    public List<PairedDevice> Devices { get; set; } = [];
}

/// <summary>A device paired with this computer.</summary>
public sealed class PairedDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What the device called itself when it paired ("Nate's iPad").</summary>
    public string Name { get; set; } = "Device";

    public RemoteRole Role { get; set; }

    /// <summary>SHA-256 of its token, hex. The token itself is only on the device.</summary>
    public string TokenHash { get; set; } = "";

    public DateTimeOffset Paired { get; set; }

    public DateTimeOffset? LastSeen { get; set; }
}
