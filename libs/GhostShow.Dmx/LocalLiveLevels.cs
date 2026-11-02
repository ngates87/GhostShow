using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;

namespace GhostShow.Dmx;

/// <summary>Live levels on the show computer: the engine's, and what the outputs last sent.</summary>
/// <remarks><see cref="CopyAsSent"/> looks through the running outputs, so call it on the show's thread.</remarks>
public sealed class LocalLiveLevels(ShowEngine engine, Func<int, DmxOutputService?> findOutput) : ILiveLevels
{
    public CommandResult SetChannel(int universe, int address, int value) => engine.SetChannel(universe, address, value);
    public CommandResult ClearUniverse(int universe) => engine.ClearUniverse(universe);
    public CommandResult SetFixtureChannel(Guid fixtureId, int channelNumber, int value) => engine.SetFixtureChannel(fixtureId, channelNumber, value);
    public CommandResult SetFixtureControl(Guid fixtureId, ChannelFunction function, int value) => engine.SetFixtureControl(fixtureId, function, value);
    public bool TryCopyLevels(int universe, Span<byte> destination) => engine.TryCopyUniverse(universe, destination, out _);
    public void CopyTimelineControlled(int universe, Span<bool> destination) => engine.CopyTimelineControlled(universe, destination);

    public string CopyAsSent(int universe, Span<byte> destination)
    {
        if (findOutput(universe) is { } output)
        {
            output.CopyLastSentFrame(destination);
            return $"Universe {universe} as sent to {output.Device} ({output.Status})";
        }
        if (engine.TryCopyUniverse(universe, destination, out _)) return $"Universe {universe} as held by the engine (no output running)";
        destination.Clear();
        return $"The show has no universe {universe}.";
    }

    public void Watch(int? universe) { }   // the levels are right here
}
