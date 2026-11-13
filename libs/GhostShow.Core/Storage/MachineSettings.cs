using System.Text.Json.Serialization;

namespace GhostShow.Core.Storage;

/// <summary>
/// Settings that belong to this computer rather than to a show: which hardware outputs which universe, and where to
/// find fixture profiles. Saved in the user's config directory (see <see cref="AppPaths"/>).
/// </summary>
public sealed class MachineSettings
{
    /// <summary>2: universes numbered from 1 (version 1 numbered them from 0; loading shifts them up).</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Universes without a binding go to the simulator, so the monitor always works.</summary>
    public List<OutputBinding> Outputs { get; set; } = [];

    /// <summary>
    /// Extra folders to load fixture profiles from: GhostShow .json profiles and QLC+ .qxf files. The user profile
    /// folder and QLC+'s standard install folders are always searched; this is for anything else.
    /// </summary>
    public List<string> ProfileDirectories { get; set; } = [];

    /// <summary>Audio output device and master volume for this computer.</summary>
    public AudioSettings Audio { get; set; } = new();

    /// <summary>Which port each show input device is on, on this computer (Inputs screen), by the device's id.</summary>
    public List<InputPortBinding> InputPorts { get; set; } = [];

    /// <summary>
    /// Before shows listed their inputs, the one serial port this computer listened on. Only read, to put an older show's
    /// serial device on it (see <see cref="LegacySerialTriggers"/>); not written once that's done.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SerialInputSettings? SerialInput { get; set; }

    /// <summary>The web remote: whether devices on the network may connect, and the paired devices.</summary>
    public RemoteSettings Remote { get; set; } = new();

    /// <summary>Shows opened or saved on this computer, most recent first (show folders, or older single-file shows).</summary>
    public List<string> RecentShows { get; set; } = [];

    public const int MaxRecentShows = 10;

    /// <summary>Puts a show at the top of the recent list (once), keeping at most <see cref="MaxRecentShows"/>.</summary>
    public void AddRecentShow(string path)
    {
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        RemoveRecentShow(path);
        RecentShows.Insert(0, path);
        if (RecentShows.Count > MaxRecentShows) RecentShows.RemoveRange(MaxRecentShows, RecentShows.Count - MaxRecentShows);
    }

    public void RemoveRecentShow(string path)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        RecentShows.RemoveAll(p => string.Equals(p, path, comparison));
    }

    public OutputDeviceConfig? FindOutput(int universe) => Outputs.FirstOrDefault(o => o.Universe == universe)?.Device;

    /// <summary>
    /// Takes output assignments migrated out of a version 1 show file, for universes that don't have one yet.
    /// Returns how many were taken; the caller saves the settings if any were.
    /// </summary>
    public int AdoptMigratedOutputs(IEnumerable<OutputBinding> migrated)
    {
        int adopted = 0;
        foreach (var binding in migrated.Where(b => FindOutput(b.Universe) is null))
        {
            SetOutput(binding.Universe, binding.Device);
            adopted++;
        }
        return adopted;
    }

    public void SetOutput(int universe, OutputDeviceConfig? device)
    {
        Outputs.RemoveAll(o => o.Universe == universe);
        if (device is not null) Outputs.Add(new OutputBinding(universe, device));
        Outputs.Sort((a, b) => a.Universe.CompareTo(b.Universe));
    }
}

public sealed record OutputBinding(int Universe, OutputDeviceConfig Device);

/// <summary>Which device to use. Records, so an unchanged binding compares equal and its output isn't restarted.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SimulatorDeviceConfig), "simulator")]
[JsonDerivedType(typeof(OpenDmxDeviceConfig), "openDmx")]
[JsonDerivedType(typeof(UsbDmxProDeviceConfig), "usbDmxPro")]
[JsonDerivedType(typeof(ArtNetDeviceConfig), "artNet")]
public abstract record OutputDeviceConfig;

public sealed record SimulatorDeviceConfig : OutputDeviceConfig;

/// <param name="Port">Where the adapter was last seen. Ports change when a cable moves USB socket.</param>
/// <param name="SerialNumber">The FTDI chip's USB serial number: the adapter's real identity.</param>
public sealed record OpenDmxDeviceConfig(string Port, string? SerialNumber) : OutputDeviceConfig;

/// <param name="SerialNumber">The widget serial number the adapter reports over the Pro protocol.</param>
public sealed record UsbDmxProDeviceConfig(string Port, string? SerialNumber) : OutputDeviceConfig;

/// <param name="Address">Node IP for unicast; null broadcasts.</param>
public sealed record ArtNetDeviceConfig(string? Address, int ArtNetUniverse) : OutputDeviceConfig;

/// <param name="DeviceName">The output device to use; null for the system default.</param>
/// <param name="MasterVolume">0-1, applied on top of every audio event's own volume.</param>
public sealed record AudioSettings(string? DeviceName = null, double MasterVolume = 1.0);

/// <summary>The one serial port a computer listened on before shows listed their inputs (read only to carry it over).</summary>
public sealed record SerialInputSettings(string? Port = null, int BaudRate = 9600, bool Enabled = true);

/// <summary>Where one of the show's input devices is on this computer.</summary>
/// <param name="Input">The show's input device (<see cref="Model.Project.Inputs"/>).</param>
/// <param name="Port">The serial port ("COM5", "/dev/ttyUSB1").</param>
/// <param name="UsbSerial">The USB serial number of the device on it, when it has one: a port name moves to whatever is
/// plugged in first, so the device is looked for by this and followed to its new port.</param>
/// <param name="Enabled">Off keeps the port picked but closed, e.g. to let another program use it.</param>
public sealed record InputPortBinding(Guid Input, string Port, string? UsbSerial = null, bool Enabled = true);
