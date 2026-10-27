using GhostShow.Core.Fixtures;
using GhostShow.Core.Timeline;
using static GhostShow.Tests.Timelines.TimelineTestKit;

namespace GhostShow.Tests.Timelines;

/// <summary>Lighting tracks with channels: points on a line per channel, and how they play.</summary>
public class ChannelLaneTests
{
    static CurvePoint P(double seconds, byte value, bool step = false) => new() { Time = S(seconds), Value = value, Step = step };

    static ChannelLane Lane(LaneTarget target, params CurvePoint[] points) => new() { Target = target, Points = [.. points] };

    static TimelineTrack Track(params ChannelLane[] lanes) => new() { Name = "Lights", Kind = TrackKind.Lighting, Channels = [.. lanes] };

    static byte At(CompiledTimeline compiled, double seconds, int address) => LevelsAt(compiled, seconds).Values[address - 1];

    [Fact]
    public void A_line_fades_between_points_and_holds_after_the_last()
    {
        var compiled = Compile(Timeline(Track(Lane(LaneTarget.Raw(1, 10), P(1, 0), P(3, 200), P(5, 100)))));

        Assert.Equal(0, At(compiled, 1, 10));
        Assert.Equal(100, At(compiled, 2, 10));
        Assert.Equal(200, At(compiled, 3, 10));
        Assert.Equal(150, At(compiled, 4, 10));
        Assert.Equal(100, At(compiled, 60, 10));
        Assert.Equal(S(5).Ticks, compiled.Length);
    }

    [Fact]
    public void Before_its_first_point_the_channel_is_left_to_manual_control()
    {
        var compiled = Compile(Timeline(Track(Lane(LaneTarget.Raw(1, 10), P(2, 255)))));

        Assert.False(LevelsAt(compiled, 1.9).Controlled[9]);
        Assert.True(LevelsAt(compiled, 2).Controlled[9]);
    }

    [Fact]
    public void A_step_point_holds_the_previous_level_then_jumps()
    {
        var compiled = Compile(Timeline(Track(Lane(LaneTarget.Raw(1, 10), P(0, 50), P(4, 250, step: true)))));

        Assert.Equal(50, At(compiled, 3.99, 10));
        Assert.Equal(250, At(compiled, 4, 10));
    }

    [Fact]
    public void Points_at_the_same_time_jump_and_the_later_one_wins()
    {
        var compiled = Compile(Timeline(Track(Lane(LaneTarget.Raw(1, 10), P(0, 0), P(2, 100), P(2, 10), P(4, 210)))));

        Assert.Equal(50, At(compiled, 1, 10));
        Assert.Equal(10, At(compiled, 2, 10));
        Assert.Equal(110, At(compiled, 3, 10));
    }

    [Fact]
    public void Points_out_of_order_play_in_time_order()
    {
        var compiled = Compile(Timeline(Track(Lane(LaneTarget.Raw(1, 10), P(4, 200), P(0, 0)))));

        Assert.Equal(100, At(compiled, 2, 10));
    }

    [Fact]
    public void A_fixture_lane_follows_its_fixture_and_its_control()
    {
        var par = TestProfiles.Fixture("Par", startAddress: 20);
        var context = Context(TestProfiles.Patch(par).Fixtures);
        var timeline = Timeline(Track(Lane(LaneTarget.FixtureControl(par.Id, ChannelFunction.Red), P(0, 255)),
                                      Lane(LaneTarget.FixtureChannel(par.Id, 5), P(0, 77))));

        var compiled = Compile(timeline, context);

        Assert.Equal(255, At(compiled, 1, 21));   // Red is the fixture's second channel
        Assert.Equal(77, At(compiled, 1, 24));    // its fifth (White)
        Assert.Empty(compiled.Issues);
    }

    [Fact]
    public void A_lane_whose_fixture_is_gone_or_lacks_the_control_is_reported_and_skipped()
    {
        var par = TestProfiles.Fixture("Par", startAddress: 1, mode: "4ch");
        var gone = Lane(LaneTarget.FixtureControl(Guid.NewGuid(), ChannelFunction.Intensity), P(0, 255));
        var noDimmer = Lane(LaneTarget.FixtureControl(par.Id, ChannelFunction.Intensity), P(0, 255));
        var fine = Lane(LaneTarget.FixtureControl(par.Id, ChannelFunction.Blue), P(0, 9));

        var compiled = Compile(Timeline(Track(gone, noDimmer, fine)), Context(TestProfiles.Patch(par).Fixtures));

        Assert.Equal([gone.Id, noDimmer.Id], compiled.Issues.Select(i => i.EventId!.Value));
        Assert.Equal(9, At(compiled, 1, 3));
    }

    [Fact]
    public void A_raw_lane_on_an_undefined_universe_is_reported()
    {
        var lane = Lane(LaneTarget.Raw(7, 1), P(0, 255));

        var compiled = Compile(Timeline(Track(lane)));

        Assert.Equal(lane.Id, Assert.Single(compiled.Issues).EventId);
    }

    [Fact]
    public void A_muted_track_s_lanes_do_not_play()
    {
        var track = Track(Lane(LaneTarget.Raw(1, 10), P(0, 255)));
        track.Enabled = false;

        Assert.False(LevelsAt(Compile(Timeline(track)), 1).Controlled[9]);
    }

    [Fact]
    public void Value_at_matches_what_plays()
    {
        var lane = Lane(LaneTarget.Raw(1, 10), P(1, 0), P(3, 200), P(5, 50, step: true));
        var compiled = Compile(Timeline(Track(lane)));

        foreach (var t in new[] { 0.5, 1, 1.5, 2.9, 3, 4.99, 5, 9 })
            Assert.Equal(lane.ValueAt(S(t)) ?? 0, At(compiled, t, 10));
        Assert.Null(lane.ValueAt(S(0.5)));
    }

    // ---- converting lighting events ----

    [Fact]
    public void A_fade_event_becomes_a_jump_to_its_start_level_and_a_point_at_its_end()
    {
        var timeline = Timeline(Lights(Raw(1, 10, to: 200, fade: 2, from: 50)));

        Assert.True(LevelEventConversion.Convert(timeline));

        var track = timeline.Tracks[0];
        Assert.Empty(track.Events);
        var lane = Assert.Single(track.Channels);
        Assert.Equal(LaneTarget.Raw(1, 10), lane.Target);
        Assert.Equal([(S(1), 50, true), (S(3), 200, false)], lane.Points.Select(p => (p.Time, (int)p.Value, p.Step)));
    }

    [Fact]
    public void Converted_events_play_the_same_as_before()
    {
        // A fade, an instant change cutting into it, a fade from wherever the channel was, and another channel.
        LevelsEvent[] events = [Raw(0, 10, to: 200, fade: 4, from: 0), Raw(2, 10, to: 20), Raw(3, 10, to: 255, fade: 2), Raw(1, 11, to: 90, fade: 1)];
        var before = Compile(Timeline(Lights(events.Select(e => e.Clone()).ToArray())));
        var timeline = Timeline(Lights(events));

        LevelEventConversion.Convert(timeline);
        var after = Compile(timeline);

        for (double t = 0; t <= 6; t += 0.25)
        {
            var (expected, expectedControlled) = LevelsAt(before, t);
            var (actual, actualControlled) = LevelsAt(after, t);
            Assert.True(expected.AsSpan(0, 12).SequenceEqual(actual.AsSpan(0, 12)), $"levels differ at {t} s");
            Assert.True(expectedControlled.AsSpan(0, 12).SequenceEqual(actualControlled.AsSpan(0, 12)), $"control differs at {t} s");
        }
    }

    [Fact]
    public void A_fade_that_carries_on_from_where_the_last_one_ended_adds_no_extra_point()
    {
        var timeline = Timeline(Lights(Raw(0, 10, to: 120, fade: 4, from: 0), Raw(4, 10, to: 20, fade: 4)));

        LevelEventConversion.Convert(timeline);

        Assert.Equal([(0.0, 0), (4, 120), (8, 20)], timeline.Tracks[0].Channels[0].Points.Select(p => (p.Time.TotalSeconds, (int)p.Value)));
    }

    [Fact]
    public void Fixture_events_become_lanes_per_control_and_disabled_events_are_dropped()
    {
        var fixtureId = Guid.NewGuid();
        var on = new LevelsEvent { FixtureId = fixtureId, Start = S(1), Levels = [new Level(255, Control: ChannelFunction.Intensity), new Level(9, Channel: 3)] };
        var off = new LevelsEvent { FixtureId = fixtureId, Start = S(2), Enabled = false, Levels = [new Level(0, Control: ChannelFunction.Intensity)] };
        var timeline = Timeline(Lights(on, off));

        LevelEventConversion.Convert(timeline);

        Assert.Equal([LaneTarget.FixtureControl(fixtureId, ChannelFunction.Intensity), LaneTarget.FixtureChannel(fixtureId, 3)],
                     timeline.Tracks[0].Channels.Select(l => l.Target));
        Assert.All(timeline.Tracks[0].Channels, l => Assert.Single(l.Points));
    }

    [Fact]
    public void A_timeline_without_lighting_events_is_left_alone()
    {
        var timeline = Timeline(new TimelineTrack { Kind = TrackKind.Markers, Events = [new MarkerEvent { Name = "A" }] });

        Assert.False(LevelEventConversion.Convert(timeline));
        Assert.Single(timeline.Tracks[0].Events);
    }

    // ---- blocks ----

    static LightingClip Block(double start, double length, ChannelLane lane) =>
        new() { Start = S(start), Length = S(length), Lanes = [lane] };

    static TimelineTrack BlockTrack(LaneTarget target, params LightingClip[] blocks) => new()
    {
        Name = "Lights", Kind = TrackKind.Lighting, Channels = [new ChannelLane { Target = target }], Events = [.. blocks],
    };

    [Fact]
    public void A_block_plays_its_lines_at_its_position_and_the_last_level_holds_after_it()
    {
        var target = LaneTarget.Raw(1, 10);
        var compiled = Compile(Timeline(BlockTrack(target, Block(2, 4, Lane(target, P(0, 0), P(4, 200))))));

        Assert.False(LevelsAt(compiled, 1.9).Controlled[9]);
        Assert.Equal(100, At(compiled, 4, 10));
        Assert.Equal(200, At(compiled, 30, 10));
        Assert.Equal(S(6).Ticks, compiled.Length);
    }

    [Fact]
    public void Moving_a_block_moves_its_lines_and_a_later_block_takes_over_from_its_start()
    {
        var target = LaneTarget.Raw(1, 10);
        var first = Block(0, 10, Lane(target, P(0, 0), P(10, 250)));
        var second = Block(4, 2, Lane(target, P(0, 50)));
        var compiled = Compile(Timeline(BlockTrack(target, first, second)));

        Assert.Equal(75, At(compiled, 3, 10));
        Assert.Equal(50, At(compiled, 5, 10));   // the later block's level from its start on
    }

    [Fact]
    public void Stretching_a_block_scales_its_points()
    {
        var target = LaneTarget.Raw(1, 10);
        var block = Block(0, 4, Lane(target, P(0, 0), P(1, 100), P(4, 200)));

        block.Stretch(S(8));

        Assert.Equal([S(0), S(2), S(8)], block.Lanes[0].Points.Select(p => p.Time));
        Assert.Equal(S(8), block.Length);
    }

    [Fact]
    public void Block_points_past_its_end_and_channels_not_on_the_track_do_not_play()
    {
        var target = LaneTarget.Raw(1, 10);
        var other = LaneTarget.Raw(1, 11);
        var block = new LightingClip { Start = S(0), Length = S(2), Lanes = [Lane(target, P(0, 10), P(3, 250)), Lane(other, P(0, 99))] };

        var compiled = Compile(Timeline(BlockTrack(target, block)));

        Assert.Equal(10, At(compiled, 5, 10));
        Assert.False(LevelsAt(compiled, 1).Controlled[10]);
    }

    [Fact]
    public void Upgrading_moves_track_points_into_one_block_that_plays_the_same()
    {
        var target = LaneTarget.Raw(1, 10);
        var track = new TimelineTrack { Kind = TrackKind.Lighting, Channels = [Lane(target, P(2, 0), P(5, 240))] };
        var before = Compile(Timeline(track));
        var timeline = Timeline(track);

        Assert.True(LevelEventConversion.Upgrade(timeline));

        Assert.Empty(track.Channels[0].Points);
        var block = Assert.IsType<LightingClip>(Assert.Single(track.Events));
        Assert.Equal((S(2), S(3)), (block.Start, block.Length));
        var after = Compile(timeline);
        foreach (var t in new[] { 1.0, 2, 3.5, 5, 9 })
            Assert.Equal(At(before, t, 10), At(after, t, 10));
    }
}
