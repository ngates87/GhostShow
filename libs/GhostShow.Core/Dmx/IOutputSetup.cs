using System.Text.Json.Serialization;

namespace GhostShow.Core.Dmx;

/// <summary>
/// A line in the adapter list: what it is, and whether it's free or which universe has it. The optional parts let the
/// screen show it as a device (its kind, port and serial number apart); <see cref="Description"/> and
/// <see cref="Status"/> stay the whole story on their own.
/// </summary>
/// <param name="Kind">"USB Pro", "Open DMX".</param>
/// <param name="Details">"S/N EN123456 · firmware 1.44".</param>
/// <param name="Universe">The universe that has it open, or null if it's free.</param>
public sealed record AdapterRow(string Description, string Status, string? Kind = null, string? Port = null, string? Details = null, int? Universe = null)
{
    [JsonIgnore]
    public bool InUse => Universe is not null;

    /// <summary>"DMX USB Pro adapter", "Open DMX adapter": what it is, rather than the port it's on.</summary>
    [JsonIgnore]
    public string Name => Kind switch
    {
        null => Description,
        "USB Pro" => "DMX USB Pro adapter",
        _ => $"{Kind} adapter",
    };

    /// <summary>"DMX Output · COM3 · S/N EN123456".</summary>
    [JsonIgnore]
    public string Summary => Kind is null ? "DMX Output" : string.Join(" · ", new[] { "DMX Output", Port, Details }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>"Available", "In use by universe 1".</summary>
    [JsonIgnore]
    public string Assignment => Universe is { } universe ? $"In use by universe {universe}" : Status;

    public override string ToString() => $"{Description}  ·  {Status}";
}

/// <summary>How a universe's output is doing, for its status light.</summary>
public enum OutputHealth
{
    /// <summary>Not running.</summary>
    Off,
    Connecting,
    Connected,
    /// <summary>The device failed or isn't there, and it's being retried.</summary>
    Faulted,
}

/// <summary>One universe's output: how it's doing, the devices it could go out on, and the one it does.</summary>
public sealed record UniverseOutput(int Universe, string Status, bool IsFaulted, IReadOnlyList<string> Choices, string Selected,
                                    OutputHealth Health = OutputHealth.Off);

/// <summary>Everything the DMX setup screen shows about the show computer's hardware.</summary>
public sealed record OutputSetupState(string DetectStatus, IReadOnlyList<AdapterRow> Adapters, IReadOnlyList<UniverseOutput> Universes)
{
    public static readonly OutputSetupState Empty = new("Looking for adapters…", [], []);

    /// <summary>The same on screen? (A record's own comparison would only compare the lists by reference.)</summary>
    public bool SameAs(OutputSetupState other) =>
        DetectStatus == other.DetectStatus
        && Adapters.SequenceEqual(other.Adapters)
        && Universes.Count == other.Universes.Count
        && Universes.Zip(other.Universes).All(pair => pair.First with { Choices = pair.Second.Choices } == pair.Second
                                                      && pair.First.Choices.SequenceEqual(pair.Second.Choices));
}

/// <summary>
/// The show computer's DMX hardware, for the DMX setup screen: which adapters are plugged in, and which one each
/// universe goes out on. The hardware only exists on the show computer, so detection and outputs always run there
/// (<c>LocalOutputSetup</c>); a device's screen asks over the connection (<c>RemoteOutputSetup</c> in GhostShow.Browser).
/// Devices are picked by the label they were offered under, so a device can never name a port of its own.
/// </summary>
public interface IOutputSetup
{
    OutputSetupState State { get; }

    /// <summary><see cref="State"/> changed. On the show's thread on the show computer; on the UI thread on a device.</summary>
    event Action? Changed;

    /// <summary>
    /// A DMX setup screen opened or closed. Adapters are only looked for while one is open, because checking a new port
    /// briefly opens it.
    /// </summary>
    void SetWatching(object screen, bool watching);

    /// <summary>Forgets what's been found and checks every port again.</summary>
    Task ScanAgainAsync();

    /// <summary>Sends a universe out on one of its <see cref="UniverseOutput.Choices"/>. Returns why it couldn't, or null.</summary>
    Task<string?> AssignAsync(int universe, string choice);
}
