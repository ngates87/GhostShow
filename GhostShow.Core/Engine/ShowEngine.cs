using GhostShow.Core.Dmx;
using GhostShow.Core.Fixtures;

namespace GhostShow.Core.Engine;

/// <summary>
/// The single owner of what the rig should be outputting: the universes' channel values and the fixture patch.
///
/// Threading: every member is thread-safe. Commands and reads take one short lock and never do I/O under it,
/// so UI, network and output threads can call in freely. Output services pull the latest values with
/// <see cref="TryCopyUniverse"/> at their own frame rate; nothing is queued, so a burst of slider moves just
/// overwrites values and only the newest ones go out.
///
/// Merging: raw channel commands and fixture commands write the same values; the most recent write wins.
/// Timeline playback and priorities come in a later phase.
/// </summary>
public sealed class ShowEngine : IShowCommands
{
    readonly Lock _lock = new();
    Dictionary<int, DmxUniverse> _universes = new();
    Dictionary<Guid, PatchedFixture> _fixtures = new();

    public ShowEngine() : this([0], []) { }

    public ShowEngine(IReadOnlyCollection<int> universes, IReadOnlyList<PatchedFixture> fixtures) =>
        Configure(universes, fixtures);

    /// <summary>
    /// Replaces the universe list and the patch. Universes that remain keep their current values; new ones start
    /// at zero. Fixtures that are new, or moved to a different address or mode, get their channels' default
    /// values, so e.g. a shutter starts open.
    /// </summary>
    public void Configure(IReadOnlyCollection<int> universes, IReadOnlyList<PatchedFixture> fixtures)
    {
        lock (_lock)
        {
            var newUniverses = universes.Distinct().ToDictionary(n => n, n => _universes.GetValueOrDefault(n) ?? new DmxUniverse(n));
            var newFixtures = fixtures.ToDictionary(f => f.Id);

            foreach (var fixture in fixtures)
            {
                bool unchanged = _fixtures.TryGetValue(fixture.Id, out var previous)
                                 && previous.Universe == fixture.Universe
                                 && previous.StartAddress == fixture.StartAddress
                                 && previous.Mode == fixture.Mode;
                if (!unchanged && newUniverses.TryGetValue(fixture.Universe, out var universe))
                    WriteDefaults(universe, fixture);
            }

            _universes = newUniverses;
            _fixtures = newFixtures;
        }
    }

    public IReadOnlyList<int> Universes
    {
        get { lock (_lock) return _universes.Keys.Order().ToArray(); }
    }

    public IReadOnlyList<PatchedFixture> Fixtures
    {
        get { lock (_lock) return _fixtures.Values.OrderBy(f => f.Universe).ThenBy(f => f.StartAddress).ToArray(); }
    }

    /// <summary>
    /// Copies the universe's 512 values into destination (at least 512 bytes). Returns false if there is no such
    /// universe. version increases whenever the values change, so a caller can skip work when nothing did.
    /// </summary>
    public bool TryCopyUniverse(int universe, Span<byte> destination, out long version)
    {
        lock (_lock)
        {
            if (!_universes.TryGetValue(universe, out var u))
            {
                version = 0;
                return false;
            }
            u.CopyTo(destination);
            version = u.Version;
            return true;
        }
    }

    public CommandResult SetChannel(int universe, int address, int value)
    {
        if (!DmxUniverse.IsValidAddress(address)) return CommandResult.Fail($"Address {address} is out of range; DMX addresses run from 1 to 512.");
        if (!IsValidValue(value)) return InvalidValue(value);
        lock (_lock)
        {
            if (!_universes.TryGetValue(universe, out var u)) return NoSuchUniverse(universe);
            u.SetChannel(address, (byte)value);
        }
        return CommandResult.Ok;
    }

    public CommandResult ClearUniverse(int universe)
    {
        lock (_lock)
        {
            if (!_universes.TryGetValue(universe, out var u)) return NoSuchUniverse(universe);
            u.Clear();
        }
        return CommandResult.Ok;
    }

    public CommandResult Blackout()
    {
        lock (_lock)
        {
            foreach (var u in _universes.Values) u.Clear();
        }
        return CommandResult.Ok;
    }

    /// <summary>Overwrites a whole universe; used by the (frozen) timeline player, which renders full frames.</summary>
    public CommandResult ReplaceUniverse(int universe, ReadOnlySpan<byte> values)
    {
        lock (_lock)
        {
            if (!_universes.TryGetValue(universe, out var u)) return NoSuchUniverse(universe);
            u.SetAll(values);
        }
        return CommandResult.Ok;
    }

    public CommandResult SetFixtureChannel(Guid fixtureId, int channelNumber, int value)
    {
        if (!IsValidValue(value)) return InvalidValue(value);
        lock (_lock)
        {
            if (!_fixtures.TryGetValue(fixtureId, out var fixture)) return NoSuchFixture(fixtureId);
            int address = fixture.AddressOfChannel(channelNumber);
            if (address == 0) return CommandResult.Fail($"'{fixture.Name}' has channels 1-{fixture.ChannelCount}; there is no channel {channelNumber}.");
            _universes[fixture.Universe].SetChannel(address, (byte)value);
        }
        return CommandResult.Ok;
    }

    public CommandResult SetFixtureControl(Guid fixtureId, ChannelFunction function, int value)
    {
        if (!IsValidValue(value)) return InvalidValue(value);
        lock (_lock)
        {
            if (!_fixtures.TryGetValue(fixtureId, out var fixture)) return NoSuchFixture(fixtureId);
            if (!fixture.TryGetAddress(function, out int address))
                return CommandResult.Fail($"'{fixture.Name}' ({fixture.Mode.Name}) has no {function} channel.");
            _universes[fixture.Universe].SetChannel(address, (byte)value);
        }
        return CommandResult.Ok;
    }

    static void WriteDefaults(DmxUniverse universe, PatchedFixture fixture)
    {
        for (int i = 0; i < fixture.ChannelCount; i++)
            universe.SetChannel(fixture.StartAddress + i, fixture.Mode.Channels[i].DefaultValue);
    }

    static bool IsValidValue(int value) => value is >= 0 and <= 255;
    static CommandResult InvalidValue(int value) => CommandResult.Fail($"Value {value} is out of range; DMX values run from 0 to 255.");
    static CommandResult NoSuchUniverse(int universe) => CommandResult.Fail($"The show has no universe {universe}.");
    static CommandResult NoSuchFixture(Guid id) => CommandResult.Fail($"No patched fixture has ID {id}. It may be disabled or have a patch error.");
}
