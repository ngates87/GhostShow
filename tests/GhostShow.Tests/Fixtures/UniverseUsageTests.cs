using GhostShow.Core.Fixtures;
using static GhostShow.Tests.TestProfiles;

namespace GhostShow.Tests.Fixtures;

public class UniverseUsageTests
{
    static UniverseUsage Usage(params FixtureInstance[] fixtures) => new(Patch(fixtures).Fixtures);

    [Fact]
    public void Empty_universe_is_one_free_range()
    {
        var usage = Usage();

        Assert.Equal(0, usage.UsedChannels);
        Assert.Equal([new AddressRange(1, 512)], usage.FreeRanges);
    }

    [Fact]
    public void Gaps_between_fixtures_are_free_ranges()
    {
        var usage = Usage(Fixture("A", 1), Fixture("B", 11));   // 1-5 and 11-15

        Assert.Equal(10, usage.UsedChannels);
        Assert.Equal([new AddressRange(6, 10), new AddressRange(16, 512)], usage.FreeRanges);
        Assert.Empty(usage.OverlapRanges);
    }

    [Fact]
    public void Shared_addresses_are_overlap_ranges_and_counted_once()
    {
        var usage = Usage(Fixture("A", 1), Fixture("B", 4));    // 1-5 and 4-8

        Assert.Equal([new AddressRange(4, 5)], usage.OverlapRanges);
        Assert.Equal(8, usage.UsedChannels);
    }

    [Fact]
    public void First_free_start_skips_gaps_that_are_too_small()
    {
        var usage = Usage(Fixture("A", 1), Fixture("B", 9));    // free 6-8 (3 channels), then 14-512

        Assert.Equal(6, usage.FirstFreeStart(3));
        Assert.Equal(14, usage.FirstFreeStart(5));
    }

    [Fact]
    public void Openings_are_the_free_ranges_big_enough_for_the_fixture()
    {
        var usage = new UniverseUsage([new AddressRange(1, 5), new AddressRange(9, 13), new AddressRange(30, 512)]);

        Assert.Equal([new AddressRange(14, 29)], usage.OpeningsFor(4));
        Assert.Equal([new AddressRange(6, 8), new AddressRange(14, 29)], usage.OpeningsFor(3));
        Assert.Empty(usage.OpeningsFor(17));
    }

    [Fact]
    public void Full_universe_has_no_free_start()
    {
        var fixtures = Enumerable.Range(0, 102).Select(i => Fixture($"P{i}", 1 + i * 5)).ToArray();   // 1-510

        var usage = Usage(fixtures);

        Assert.Equal(511, usage.FirstFreeStart(2));
        Assert.Null(usage.FirstFreeStart(3));
    }

    [Fact]
    public void Ranges_print_like_a_console()
    {
        Assert.Equal("11-512", new AddressRange(11, 512).ToString());
        Assert.Equal("7", new AddressRange(7, 7).ToString());
    }
}
