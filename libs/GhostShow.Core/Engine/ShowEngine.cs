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
/// Merging: raw channel commands and fixture commands write the same manual values; the most recent write wins.
/// A playing timeline has its own layer (<see cref="SetTimelineLevels"/>), which takes precedence on the channels it
/// controls; manual levels apply everywhere else, and everywhere again once the timeline is released (Stop).
/// </summary>
public sealed class ShowEngine : IShowCommands
{
    readonly Lock _lock = new();
    Dictionary<int, DmxUniverse> _universes = new();

    // The timeline layer: per universe, the levels the playing timeline wants and which channels it controls. Output
    // takes the timeline's value where it controls a channel and the manual value everywhere else.
    readonly Dictionary<int, (byte[] Values, bool[] Controlled)> _timeline = new();
    long _timelineVersion;
    Dictionary<Guid, PatchedFixture> _fixtures = new();

    /// <summary>An engine with just universe 1 and nothing patched, until <see cref="Configure"/> is called.</summary>
    public ShowEngine() : this([Model.Project.FirstUniverse], []) { }

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
            version = u.Version + _timelineVersion;
            if (_timeline.TryGetValue(universe, out var layer))
            {
                for (int i = 0; i < DmxUniverse.ChannelCount; i++)
                    if (layer.Controlled[i]) destination[i] = layer.Values[i];
            }
            return true;
        }
    }

    /// <summary>
    /// Replaces the timeline layer with a frame from the playing timeline (see <see cref="Timeline.CompiledTimeline"/>).
    /// While a channel is controlled by the timeline, output follows the timeline and manual changes to that channel
    /// are kept but not visible; they show again once the timeline releases it. Universes not in the frame are released.
    /// Called by the player on every tick (100 Hz); copies, so the player can reuse its frame. No allocation after the
    /// first call per universe.
    /// </summary>
    public void SetTimelineLevels(Timeline.LevelsFrame frame)
    {
        lock (_lock)
        {
            foreach (var layer in _timeline.Values) Array.Clear(layer.Controlled);
            for (int slot = 0; slot < frame.Universes.Count; slot++)
            {
                if (!_timeline.TryGetValue(frame.Universes[slot], out var layer))
                    _timeline[frame.Universes[slot]] = layer = (new byte[DmxUniverse.ChannelCount], new bool[DmxUniverse.ChannelCount]);
                frame.Values[slot].CopyTo(layer.Values, 0);
                frame.Controlled[slot].CopyTo(layer.Controlled, 0);
            }
            _timelineVersion++;
        }
    }

    /// <summary>Ends timeline control: every channel shows its manual level again (used by Stop).</summary>
    public void ReleaseTimeline()
    {
        lock (_lock)
        {
            foreach (var layer in _timeline.Values) Array.Clear(layer.Controlled);
            _timelineVersion++;
        }
    }

    /// <summary>The manual levels of a universe (faders), without the timeline layer: what a released channel returns to.</summary>
    public bool TryCopyManual(int universe, Span<byte> destination)
    {
        lock (_lock)
        {
            if (!_universes.TryGetValue(universe, out var dmx)) return false;
            dmx.CopyTo(destination);
            return true;
        }
    }

    /// <summary>
    /// Which channels of a universe the timeline controls (destination[i] is address i + 1), in one lock, so a screen
    /// of faders can mark them. False everywhere when nothing is playing.
    /// </summary>
    public void CopyTimelineControlled(int universe, Span<bool> destination)
    {
        lock (_lock)
        {
            if (_timeline.TryGetValue(universe, out var layer)) layer.Controlled.AsSpan(0, Math.Min(destination.Length, DmxUniverse.ChannelCount)).CopyTo(destination);
            else destination.Clear();
        }
    }

    /// <summary>Whether the timeline currently controls a channel, so manual controls can say so.</summary>
    public bool IsTimelineControlled(int universe, int address)
    {
        lock (_lock)
            return DmxUniverse.IsValidAddress(address) && _timeline.TryGetValue(universe, out var layer) && layer.Controlled[address - 1];
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
