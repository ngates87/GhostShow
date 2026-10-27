using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using static GhostShow.Tests.TestProfiles;

namespace GhostShow.Tests.Engine;

public class ShowEngineTests
{
    [Fact]
    public void Channel_command_changes_the_output_frame()
    {
        var engine = new ShowEngine();

        Assert.True(engine.SetChannel(1, 3, 255).Succeeded);

        Assert.Equal(255, Frame(engine)[2]);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(513, 10)]
    [InlineData(1, -1)]
    [InlineData(1, 256)]
    public void Out_of_range_channel_commands_fail_with_a_message_and_change_nothing(int address, int value)
    {
        var engine = new ShowEngine();

        var result = engine.SetChannel(1, address, value);

        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.All(Frame(engine), v => Assert.Equal(0, v));
    }

    [Fact]
    public void Commands_for_an_unknown_universe_fail()
    {
        var engine = new ShowEngine();

        Assert.False(engine.SetChannel(9, 1, 1).Succeeded);
        Assert.False(engine.ClearUniverse(9).Succeeded);
        Assert.False(engine.TryCopyUniverse(9, new byte[512], out _));
    }

    [Fact]
    public void Blackout_clears_every_universe()
    {
        var engine = new ShowEngine([1, 2], []);
        engine.SetChannel(1, 1, 100);
        engine.SetChannel(2, 512, 100);

        engine.Blackout();

        Assert.All(Frame(engine, 1), v => Assert.Equal(0, v));
        Assert.All(Frame(engine, 2), v => Assert.Equal(0, v));
    }

    [Fact]
    public void Fixture_control_writes_the_mapped_channel()
    {
        var par = Fixture("Par", 10);
        var engine = new ShowEngine([1], Patch(par).Fixtures);

        Assert.True(engine.SetFixtureControl(par.Id, ChannelFunction.Blue, 200).Succeeded);

        Assert.Equal(200, Frame(engine)[12]);    // address 13 = Blue in 5ch mode at 10
    }

    [Fact]
    public void Fixture_control_the_mode_lacks_fails_clearly()
    {
        var par = Fixture("Par", 1, mode: "4ch");
        var engine = new ShowEngine([1], Patch(par).Fixtures);

        var result = engine.SetFixtureControl(par.Id, ChannelFunction.Intensity, 255);

        Assert.False(result.Succeeded);
        Assert.Contains("Intensity", result.Error);
    }

    [Fact]
    public void Fixture_channel_numbers_are_relative_to_the_start_address()
    {
        var par = Fixture("Par", 100);
        var engine = new ShowEngine([1], Patch(par).Fixtures);

        engine.SetFixtureChannel(par.Id, 1, 7);
        engine.SetFixtureChannel(par.Id, 5, 9);

        Assert.Equal(7, Frame(engine)[99]);
        Assert.Equal(9, Frame(engine)[103]);
        Assert.False(engine.SetFixtureChannel(par.Id, 6, 1).Succeeded);
    }

    [Fact]
    public void Newly_patched_fixture_gets_its_default_values()
    {
        var spot = Fixture("Spot", 20, profileId: "test/spot", mode: "3ch");

        var engine = new ShowEngine([1], Patch(spot).Fixtures);

        Assert.Equal(32, Frame(engine)[20]);   // shutter at address 21 starts open
    }

    [Fact]
    public void Repatching_unchanged_fixtures_keeps_their_current_values()
    {
        var spot = Fixture("Spot", 20, profileId: "test/spot", mode: "3ch");
        var engine = new ShowEngine([1], Patch(spot).Fixtures);
        engine.SetFixtureControl(spot.Id, ChannelFunction.Shutter, 0);

        engine.Configure([1], Patch(spot).Fixtures);

        Assert.Equal(0, Frame(engine)[20]);
    }

    [Fact]
    public void Universes_that_stay_configured_keep_their_values()
    {
        var engine = new ShowEngine([1], []);
        engine.SetChannel(1, 1, 77);

        engine.Configure([1, 2], []);

        Assert.Equal(77, Frame(engine, 1)[0]);
    }

    [Fact]
    public void Rapid_updates_leave_only_the_latest_value_with_no_backlog()
    {
        var engine = new ShowEngine();

        for (int i = 0; i <= 100_000; i++) engine.SetChannel(1, 1, i % 256);

        Assert.Equal(100_000 % 256, Frame(engine)[0]);
    }

    [Fact]
    public async Task Concurrent_writers_and_readers_do_not_corrupt_state()
    {
        var engine = new ShowEngine();
        var writers = Enumerable.Range(1, 4).Select(address => Task.Run(() =>
        {
            for (int i = 0; i < 20_000; i++) engine.SetChannel(1, address, i % 256);
            engine.SetChannel(1, address, address);
        }, TestContext.Current.CancellationToken));
        var reader = Task.Run(() =>
        {
            var frame = new byte[512];
            for (int i = 0; i < 20_000; i++) engine.TryCopyUniverse(1, frame, out _);
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll([.. writers, reader]);

        Assert.Equal([1, 2, 3, 4], Frame(engine)[..4]);
    }

    static byte[] Frame(ShowEngine engine, int universe = 1)
    {
        var frame = new byte[512];
        Assert.True(engine.TryCopyUniverse(universe, frame, out _));
        return frame;
    }
}
