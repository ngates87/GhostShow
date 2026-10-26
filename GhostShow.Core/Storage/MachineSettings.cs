using System.Text.Json.Serialization;

namespace GhostShow.Core.Storage;

/// <summary>
/// Settings that belong to this computer rather than to a show: which hardware outputs which universe, and where to
/// find fixture profiles. Saved in the user's config directory (see <see cref="AppPaths"/>).
/// </summary>
public sealed class MachineSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Universes without a binding go to the simulator, so the monitor always works.</summary>
    public List<OutputBinding> Outputs { get; set; } = [];

    /// <summary>
    /// Extra folders to load fixture profiles from: GhostShow .json profiles and QLC+ .qxf files. The user profile
    /// folder and QLC+'s standard install folders are always searched; this is for anything else.
    /// </summary>
    public List<string> ProfileDirectories { get; set; } = [];

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
