using GhostShow.Core.Fixtures;
using GhostShow.Core.Timeline;
using static GhostShow.Tests.Timelines.TimelineTestKit;

namespace GhostShow.Tests.Timelines;

public class TimelineEvaluationTests
{
    [Fact]
    public void Before_an_event_starts_its_channel_is_not_controlled()
    {
        var compiled = Compile(Timeline(Lights(Raw(2, address: 1, to: 200))));

        var (values, controlled) = LevelsAt(compiled, 1.999);

        Assert.False(controlled[0]);
        Assert.Equal(0, values[0]);
    }

    [Fact]
    public void A_snap_applies_exactly_at_its_start_and_holds()
    {
        var compiled = Compile(Timeline(Lights(Raw(2, address: 1, to: 200))));

        Assert.Equal(200, LevelsAt(compiled, 2).Values[0]);
        Assert.Equal(200, LevelsAt(compiled, 60).Values[0]);
        Assert.True(LevelsAt(compiled, 2).Controlled[0]);
    }

    [Theory]
    [InlineData(1.0, 0)]
    [InlineData(1.5, 50)]
    [InlineData(2.0, 100)]
    [InlineData(2.9, 190)]
    [InlineData(3.0, 200)]
    [InlineData(99, 200)]
    public void A_fade_is_evaluated_from_its_position_in_time(double at, byte expected)
    {
        var compiled = Compile(Timeline(Lights(Raw(1, address: 1, to: 200, fade: 2, from: 0))));

        Assert.Equal(expected, LevelsAt(compiled, at).Values[0]);
    }

    [Fact]
    public void A_fade_without_from_starts_at_the_timelines_level_when_it_begins()
    {
        // 0-2 s: fade 0 -> 100. At 1 s (mid-fade, level 50) a new fade to 250 starts: it must start from 50.
        var compiled = Compile(Timeline(Lights(Raw(0, 1, to: 100, fade: 2, from: 0), Raw(1, 1, to: 250, fade: 2))));

        Assert.Equal(50, LevelsAt(compiled, 1).Values[0]);
        Assert.Equal(150, LevelsAt(compiled, 2).Values[0]);
        Assert.Equal(250, LevelsAt(compiled, 3).Values[0]);
    }

    [Fact]
    public void A_fade_without_from_and_nothing_before_starts_at_zero()
    {
        var compiled = Compile(Timeline(Lights(Raw(0, 1, to: 200, fade: 2))));

        Assert.Equal(100, LevelsAt(compiled, 1).Values[0]);
    }

    [Fact]
    public void The_latest_starting_event_wins_a_channel()
    {
        var compiled = Compile(Timeline(Lights(Raw(5, 1, to: 30), Raw(1, 1, to: 200))));

        Assert.Equal(200, LevelsAt(compiled, 4).Values[0]);
        Assert.Equal(30, LevelsAt(compiled, 5).Values[0]);
    }

    [Fact]
    public void On_a_tie_the_later_track_wins_regardless_of_event_order_in_the_file()
    {
        var compiled = Compile(Timeline(Lights(Raw(1, 1, to: 10)), Lights(Raw(1, 1, to: 20))));

        Assert.Equal(20, LevelsAt(compiled, 1).Values[0]);
    }

    [Fact]
    public void Events_on_different_channels_do_not_interfere()
    {
        var compiled = Compile(Timeline(Lights(Raw(0, 1, to: 10), Raw(0, 2, to: 20, fade: 2, from: 0)), Lights(Raw(1, 512, to: 30))));

        var (values, controlled) = LevelsAt(compiled, 1);

        Assert.Equal((10, 10, 30), (values[0], values[1], values[511]));
        Assert.Equal(3, controlled.Count(c => c));
    }

    [Fact]
    public void Disabled_tracks_and_events_produce_no_output()
    {
        var mutedTrack = Lights(Raw(0, 1, to: 10));
        mutedTrack.Enabled = false;
        var disabledEvent = Raw(0, 2, to: 20);
        disabledEvent.Enabled = false;

        var compiled = Compile(Timeline(mutedTrack, Lights(disabledEvent)));

        Assert.All(LevelsAt(compiled, 1).Controlled, c => Assert.False(c));
    }

    [Fact]
    public void Several_levels_in_one_event_apply_together()
    {
        var e = new LevelsEvent { Universe = 2, Levels = [new Level(1, Channel: 10), new Level(2, Channel: 11), new Level(3, Channel: 12)] };

        var compiled = Compile(Timeline(Lights(e)));

        Assert.Equal([1, 2, 3], LevelsAt(compiled, 0, universe: 2).Values[9..12]);
    }

    [Fact]
    public void Fixture_controls_and_channel_numbers_resolve_through_the_patch()
    {
        var instance = new FixtureInstance { Name = "Par", ProfileId = "test/rgbw", ModeName = "5ch", StartAddress = 100 };
        var patched = FixturePatcher.Patch([instance], [1], TestProfiles.Library).Fixtures;
        var e = new LevelsEvent { FixtureId = instance.Id, Levels = [new Level(255, Control: ChannelFunction.Intensity), new Level(80, Channel: 3)] };

        var compiled = Compile(Timeline(Lights(e)), Context(patched));

        var values = LevelsAt(compiled, 0).Values;
        Assert.Equal(255, values[99]);   // dimmer at 100
        Assert.Equal(80, values[101]);   // channel 3 = address 102
        Assert.Empty(compiled.Issues);
    }

    [Fact]
    public void Missing_fixtures_and_bad_targets_are_reported_and_skipped()
    {
        var missing = new LevelsEvent { Name = "Ghost light", FixtureId = Guid.NewGuid(), Levels = [new Level(255, Control: ChannelFunction.Red)] };
        var badAddress = Raw(0, address: 513, to: 1);
        var badUniverse = Raw(0, address: 1, to: 1, universe: 9);

        var compiled = Compile(Timeline(Lights(missing, badAddress, badUniverse, Raw(0, 5, to: 50))));

        Assert.Equal(3, compiled.Issues.Count);
        Assert.Contains(compiled.Issues, i => i.EventId == missing.Id && i.Message.Contains("isn't in the show"));
        Assert.Contains(compiled.Issues, i => i.Message.Contains("universe 9"));
        Assert.Equal(50, LevelsAt(compiled, 0).Values[4]);   // the valid event still plays
    }

    [Fact]
    public void Negative_times_are_rejected()
    {
        var early = Raw(-1, 1, to: 10);

        var compiled = Compile(Timeline(Lights(early)));

        Assert.Contains(compiled.Issues, i => i.EventId == early.Id && i.Message.Contains("before 0"));
        Assert.Equal(0, compiled.LevelStepCount);
    }

    [Fact]
    public void Length_is_explicit_or_the_end_of_the_last_event()
    {
        var timeline = Timeline(Lights(Raw(1, 1, to: 10, fade: 4), Raw(2, 1, to: 1)));

        Assert.Equal(T(5), Compile(timeline).Length);
        timeline.Length = S(30);
        Assert.Equal(T(30), Compile(timeline).Length);
    }
}
