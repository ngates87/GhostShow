using GhostShow.Core.Fixtures;

namespace GhostShow.Core.Engine;

/// <summary>
/// The levels the lights are at right now, and setting them by hand: what the Control, Channels and Monitor screens work
/// on. The levels live on the show computer (<c>LocalLiveLevels</c>); a device's screens get them over the connection
/// (<c>RemoteLiveLevels</c> in GhostShow.Browser).
/// </summary>
public interface ILiveLevels
{
    CommandResult SetChannel(int universe, int address, int value);
    CommandResult ClearUniverse(int universe);
    CommandResult SetFixtureChannel(Guid fixtureId, int channelNumber, int value);
    CommandResult SetFixtureControl(Guid fixtureId, ChannelFunction function, int value);

    /// <summary>A universe's levels now (destination[i] is address i + 1). False if they aren't known.</summary>
    bool TryCopyLevels(int universe, Span<byte> destination);

    /// <summary>Which channels of a universe the playing timeline controls (destination[i] is address i + 1).</summary>
    void CopyTimelineControlled(int universe, Span<bool> destination);

    /// <summary>A universe's levels as last sent to its output, for the monitor. Returns where they came from, to show.</summary>
    string CopyAsSent(int universe, Span<byte> destination);

    /// <summary>The universe a screen on show needs levels for (null: none). Only a device has to ask for them.</summary>
    void Watch(int? universe);
}
