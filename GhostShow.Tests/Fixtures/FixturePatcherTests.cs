using GhostShow.Core.Fixtures;
using static GhostShow.Tests.TestProfiles;

namespace GhostShow.Tests.Fixtures;

public class FixturePatcherTests
{
    [Fact]
    public void Valid_fixtures_are_patched_without_issues()
    {
        var result = Patch(Fixture("Left", 1), Fixture("Right", 6));

        Assert.Empty(result.Issues);
        Assert.Equal(2, result.Fixtures.Count);
    }

    [Fact]
    public void Fixture_with_a_missing_profile_is_reported_and_not_patched()
    {
        var result = Patch(Fixture("Ghost", 1, profileId: "nobody/nothing"));

        var issue = Assert.Single(result.Issues);
        Assert.Equal(PatchIssueSeverity.Error, issue.Severity);
        Assert.Contains("nobody/nothing", issue.Message);
        Assert.Empty(result.Fixtures);
    }

    [Fact]
    public void Fixture_with_a_missing_mode_lists_the_modes_that_exist()
    {
        var result = Patch(Fixture("Par", 1, mode: "12ch"));

        var issue = Assert.Single(result.Issues);
        Assert.Contains("'5ch'", issue.Message);
        Assert.Contains("'4ch'", issue.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(513)]
    [InlineData(-4)]
    public void Start_address_outside_1_to_512_is_an_error(int startAddress)
    {
        var result = Patch(Fixture("Par", startAddress));

        Assert.True(result.HasErrors);
        Assert.Empty(result.Fixtures);
    }

    [Fact]
    public void Fixture_running_past_channel_512_is_an_error_that_says_where_it_would_fit()
    {
        var result = Patch(Fixture("Par", 509));   // 5 channels: 509-513

        var issue = Assert.Single(result.Issues);
        Assert.Contains("513", issue.Message);
        Assert.Contains("508", issue.Message);
        Assert.Empty(result.Fixtures);
    }

    [Fact]
    public void Fixture_ending_exactly_on_channel_512_is_valid()
    {
        var result = Patch(Fixture("Par", 508));

        Assert.Empty(result.Issues);
        Assert.Equal(512, result.Fixtures[0].EndAddress);
    }

    [Fact]
    public void Overlapping_fixtures_are_reported_with_the_shared_addresses()
    {
        var result = Patch(Fixture("Left", 1), Fixture("Right", 4));   // 1-5 and 4-8

        var issue = Assert.Single(result.Issues);
        Assert.Equal(PatchIssueSeverity.Warning, issue.Severity);
        Assert.Contains("addresses 4-5", issue.Message);
        Assert.Equal(2, result.Fixtures.Count);
    }

    [Fact]
    public void Adjacent_fixtures_do_not_overlap()
    {
        Assert.Empty(Patch(Fixture("Left", 1), Fixture("Right", 6)).Issues);
    }

    [Fact]
    public void Fixtures_on_different_universes_never_overlap()
    {
        var left = Fixture("Left", 1);
        var right = Fixture("Right", 1, universe: 2);

        var result = FixturePatcher.Patch([left, right], [1, 2], Library);

        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Disabled_fixtures_are_neither_patched_nor_checked_for_overlap()
    {
        var disabled = Fixture("Spare", 1);
        disabled.Enabled = false;

        var result = Patch(Fixture("Left", 1), disabled);

        Assert.Empty(result.Issues);
        Assert.Single(result.Fixtures);
    }

    [Fact]
    public void Fixture_on_a_universe_the_show_does_not_define_is_an_error()
    {
        var result = Patch(Fixture("Par", 1, universe: 7));

        Assert.Contains("universe 7", Assert.Single(result.Issues).Message);
    }

    [Fact]
    public void Two_fixtures_with_the_same_id_are_an_error()
    {
        var first = Fixture("A", 1);
        var second = Fixture("B", 10);
        second.Id = first.Id;

        Assert.True(Patch(first, second).HasErrors);
    }

    [Fact]
    public void Logical_controls_map_to_the_fixture_s_dmx_addresses()
    {
        var fixture = Patch(Fixture("Par", 10)).Fixtures[0];

        Assert.True(fixture.TryGetAddress(ChannelFunction.Intensity, out int dimmer));
        Assert.True(fixture.TryGetAddress(ChannelFunction.White, out int white));
        Assert.Equal(10, dimmer);
        Assert.Equal(14, white);
        Assert.False(fixture.HasControl(ChannelFunction.Pan));
    }

    [Fact]
    public void Same_profile_maps_differently_in_another_mode()
    {
        var fixture = Patch(Fixture("Par", 10, mode: "4ch")).Fixtures[0];

        Assert.False(fixture.HasControl(ChannelFunction.Intensity));
        Assert.True(fixture.TryGetAddress(ChannelFunction.Red, out int red));
        Assert.Equal(10, red);
    }
}
