namespace GhostShow.Core.Dmx;

/// <summary>A line in the adapter list: what it is, and whether it's free or which universe has it.</summary>
public sealed record AdapterRow(string Description, string Status)
{
    public override string ToString() => $"{Description}  ·  {Status}";
}

/// <summary>One universe's output: how it's doing, the devices it could go out on, and the one it does.</summary>
public sealed record UniverseOutput(int Universe, string Status, bool IsFaulted, IReadOnlyList<string> Choices, string Selected);

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
