using GhostShow.Core.Dmx;

namespace GhostShow.Core.Fixtures;

public enum PatchIssueSeverity { Warning, Error }

/// <param name="FixtureId">The fixture the problem is about, or null for a show-wide problem.</param>
public sealed record PatchIssue(PatchIssueSeverity Severity, Guid? FixtureId, string Message)
{
    public override string ToString() => $"{Severity}: {Message}";
}

/// <summary>A validated fixture resolved to concrete DMX addresses. Immutable; the engine looks controls up here.</summary>
public sealed class PatchedFixture
{
    // Indexed by (int)ChannelFunction; 0 = the mode has no channel with that function.
    readonly int[] _addressByFunction = new int[Enum.GetValues<ChannelFunction>().Length];

    public PatchedFixture(FixtureInstance instance, FixtureProfile profile, FixtureMode mode)
    {
        Id = instance.Id;
        Name = instance.Name;
        Universe = instance.Universe;
        StartAddress = instance.StartAddress;
        Profile = profile;
        Mode = mode;

        // Walk backwards so the first channel with a function wins (e.g. "Dimmer" before "Dimmer fine").
        for (int i = mode.Channels.Count - 1; i >= 0; i--)
            _addressByFunction[(int)mode.Channels[i].Function] = StartAddress + i;
    }

    public Guid Id { get; }
    public string Name { get; }
    public int Universe { get; }
    public int StartAddress { get; }
    public FixtureProfile Profile { get; }
    public FixtureMode Mode { get; }
    public int ChannelCount => Mode.Channels.Count;
    public int EndAddress => StartAddress + ChannelCount - 1;

    public bool HasControl(ChannelFunction function) => _addressByFunction[(int)function] != 0;

    public bool TryGetAddress(ChannelFunction function, out int address)
    {
        address = _addressByFunction[(int)function];
        return address != 0;
    }

    /// <summary>DMX address of the fixture's channel N (1-based within the mode), or 0 if it has no channel N.</summary>
    public int AddressOfChannel(int channelNumber) =>
        channelNumber >= 1 && channelNumber <= ChannelCount ? StartAddress + channelNumber - 1 : 0;
}

public sealed record PatchResult(IReadOnlyList<PatchedFixture> Fixtures, IReadOnlyList<PatchIssue> Issues)
{
    public bool HasErrors => Issues.Any(i => i.Severity == PatchIssueSeverity.Error);
}

/// <summary>
/// Turns the fixtures in a show into a DMX patch and reports everything wrong with them.
///
/// A fixture with an error (missing profile or mode, bad address, unknown universe) is left out of the patch,
/// since the engine can't know where its channels are. Overlapping fixtures are only a warning: both stay patched
/// and the most recent write to a shared channel wins, which is how people sometimes deliberately double-patch.
/// Disabled fixtures are not patched and are not checked for overlaps.
/// </summary>
public static class FixturePatcher
{
    public static PatchResult Patch(IEnumerable<FixtureInstance> fixtures, IReadOnlyCollection<int> universes,
                                    FixtureProfileLibrary profiles)
    {
        var patched = new List<PatchedFixture>();
        var issues = new List<PatchIssue>();
        var seenIds = new HashSet<Guid>();

        foreach (var fixture in fixtures)
        {
            if (!seenIds.Add(fixture.Id))
            {
                issues.Add(Error(fixture, $"'{fixture.Name}' has the same ID as another fixture ({fixture.Id}); give it a new one."));
                continue;
            }
            if (!fixture.Enabled) continue;

            var resolved = Resolve(fixture, universes, profiles, issues);
            if (resolved is not null) patched.Add(resolved);
        }

        issues.AddRange(FindOverlaps(patched));
        return new PatchResult(patched, issues);
    }

    static PatchedFixture? Resolve(FixtureInstance fixture, IReadOnlyCollection<int> universes,
                                   FixtureProfileLibrary profiles, List<PatchIssue> issues)
    {
        var profile = profiles.Find(fixture.ProfileId);
        if (profile is null)
        {
            issues.Add(Error(fixture, $"'{fixture.Name}' uses fixture profile '{fixture.ProfileId}', which isn't in the profile library."));
            return null;
        }

        var mode = profile.FindMode(fixture.ModeName);
        if (mode is null)
        {
            var available = string.Join(", ", profile.Modes.Select(m => $"'{m.Name}'"));
            issues.Add(Error(fixture, $"'{fixture.Name}': {profile} has no mode '{fixture.ModeName}'. Available: {available}."));
            return null;
        }

        if (mode.Channels.Count is 0 or > DmxUniverse.ChannelCount)
        {
            issues.Add(Error(fixture, $"'{fixture.Name}': mode '{mode.Name}' of {profile} has {mode.Channels.Count} channels; it must have 1-{DmxUniverse.ChannelCount}."));
            return null;
        }

        if (!universes.Contains(fixture.Universe))
        {
            issues.Add(Error(fixture, $"'{fixture.Name}' is on universe {fixture.Universe}, which the show doesn't define."));
            return null;
        }

        if (!DmxUniverse.IsValidAddress(fixture.StartAddress))
        {
            issues.Add(Error(fixture, $"'{fixture.Name}' starts at address {fixture.StartAddress}; addresses run from 1 to 512."));
            return null;
        }

        int endAddress = fixture.StartAddress + mode.Channels.Count - 1;
        if (endAddress > DmxUniverse.LastAddress)
        {
            int lastValidStart = DmxUniverse.LastAddress - mode.Channels.Count + 1;
            issues.Add(Error(fixture, $"'{fixture.Name}' needs {mode.Channels.Count} channels from address {fixture.StartAddress}, " +
                                      $"which runs past 512 (to {endAddress}). The highest start address it fits at is {lastValidStart}."));
            return null;
        }

        return new PatchedFixture(fixture, profile, mode);
    }

    static IEnumerable<PatchIssue> FindOverlaps(List<PatchedFixture> fixtures)
    {
        foreach (var universe in fixtures.GroupBy(f => f.Universe))
        {
            var ordered = universe.OrderBy(f => f.StartAddress).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                for (int j = i + 1; j < ordered.Count && ordered[j].StartAddress <= ordered[i].EndAddress; j++)
                {
                    var (a, b) = (ordered[i], ordered[j]);
                    int sharedFrom = b.StartAddress, sharedTo = Math.Min(a.EndAddress, b.EndAddress);
                    yield return new PatchIssue(PatchIssueSeverity.Warning, b.Id,
                        $"'{b.Name}' ({b.StartAddress}-{b.EndAddress}) overlaps '{a.Name}' ({a.StartAddress}-{a.EndAddress}) " +
                        $"on universe {universe.Key} at {(sharedFrom == sharedTo ? $"address {sharedFrom}" : $"addresses {sharedFrom}-{sharedTo}")}.");
                }
            }
        }
    }

    static PatchIssue Error(FixtureInstance fixture, string message) => new(PatchIssueSeverity.Error, fixture.Id, message);
}
