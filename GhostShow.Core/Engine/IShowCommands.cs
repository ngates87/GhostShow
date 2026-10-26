namespace GhostShow.Core.Engine;

/// <summary>
/// Everything that can change what the rig outputs. The desktop UI, the CLI and, later, OSC, the web server,
/// physical buttons and timelines all go through this interface; none of them talk to DMX hardware.
/// Every method is thread-safe, returns quickly and never waits on I/O.
/// </summary>
public interface IShowCommands
{
    /// <param name="address">1-512</param>
    /// <param name="value">0-255</param>
    CommandResult SetChannel(int universe, int address, int value);

    /// <summary>Sets every channel of the universe to zero.</summary>
    CommandResult ClearUniverse(int universe);

    /// <summary>Sets every channel of every universe to zero.</summary>
    CommandResult Blackout();

    /// <summary>Sets one of a fixture's channels. channelNumber is 1-based within the fixture's mode.</summary>
    CommandResult SetFixtureChannel(Guid fixtureId, int channelNumber, int value);

    /// <summary>
    /// Sets a logical control such as intensity or red on a fixture. Fails if the fixture's mode has no channel
    /// with that function.
    /// </summary>
    CommandResult SetFixtureControl(Guid fixtureId, Fixtures.ChannelFunction function, int value);
}
