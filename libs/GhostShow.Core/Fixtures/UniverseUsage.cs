using GhostShow.Core.Dmx;

namespace GhostShow.Core.Fixtures;

/// <summary>An inclusive range of DMX addresses, e.g. 11-512.</summary>
public readonly record struct AddressRange(int Start, int End)
{
    public int Length => End - Start + 1;

    public override string ToString() => Start == End ? Start.ToString() : $"{Start}-{End}";
}

/// <summary>Which addresses of one universe the patched fixtures occupy: used channels, free gaps and overlaps.</summary>
public sealed class UniverseUsage
{
    // Index = DMX address (0 unused): how many fixtures use that address.
    readonly int[] _occupants = new int[DmxUniverse.LastAddress + 1];

    /// <param name="fixtures">The patched fixtures on this universe (patching guarantees their ranges are within 1-512).</param>
    public UniverseUsage(IEnumerable<PatchedFixture> fixtures)
        : this(fixtures.Select(f => new AddressRange(f.StartAddress, f.EndAddress))) { }

    /// <param name="occupied">The address ranges in use; parts outside 1-512 are ignored.</param>
    public UniverseUsage(IEnumerable<AddressRange> occupied)
    {
        foreach (var range in occupied)
            for (int address = Math.Max(range.Start, DmxUniverse.FirstAddress); address <= Math.Min(range.End, DmxUniverse.LastAddress); address++)
                _occupants[address]++;

        UsedChannels = _occupants.Count(n => n > 0);
        FreeRanges = Ranges(n => n == 0);
        OverlapRanges = Ranges(n => n > 1);
    }

    public int UsedChannels { get; }

    public IReadOnlyList<AddressRange> FreeRanges { get; }

    /// <summary>Addresses used by more than one fixture.</summary>
    public IReadOnlyList<AddressRange> OverlapRanges { get; }

    /// <summary>The free ranges with room for channelCount channels in a row: where a fixture of that size can go.</summary>
    public IReadOnlyList<AddressRange> OpeningsFor(int channelCount) => FreeRanges.Where(r => r.Length >= channelCount).ToArray();

    /// <summary>The lowest start address with channelCount free addresses in a row, or null if no gap is big enough.</summary>
    public int? FirstFreeStart(int channelCount)
    {
        foreach (var range in FreeRanges)
            if (range.Length >= channelCount) return range.Start;
        return null;
    }

    IReadOnlyList<AddressRange> Ranges(Func<int, bool> matches)
    {
        var ranges = new List<AddressRange>();
        int? start = null;
        for (int address = DmxUniverse.FirstAddress; address <= DmxUniverse.LastAddress + 1; address++)
        {
            bool inRange = address <= DmxUniverse.LastAddress && matches(_occupants[address]);
            if (inRange && start is null) start = address;
            else if (!inRange && start is { } s)
            {
                ranges.Add(new AddressRange(s, address - 1));
                start = null;
            }
        }
        return ranges;
    }
}
