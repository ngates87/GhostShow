using GhostShow.Core.Fixtures;
using GhostShow.Core.Model;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;
using static GhostShow.Tests.Timelines.TimelineTestKit;

namespace GhostShow.Tests.Timelines;

/// <summary>
/// Tilt clips, with the SHEHDS beam bar (0-230° of tilt): from a start angle to an end one over the clip, both bytes of
/// a 16-bit tilt together, or the one byte in a mode without a fine channel; the track's other channels hold.
/// </summary>
public class TiltMoveTests
{
    static readonly FixtureProfile Bar = FixtureProfileLoader.LoadBuiltIn().Single(p => p.Id == "shehds/led-beam-bar-10x40w-rgbw");

    static PatchedFixture Patch(string mode, int start = 1)
    {
        var instance = new FixtureInstance { Name = "Bar", ProfileId = Bar.Id, ModeName = mode, Universe = 1, StartAddress = start };
        return new PatchedFixture(instance, Bar, Bar.FindMode(mode)!);
    }

    static ChannelLane Lane(LaneTarget target) => new() { Target = target };

    static LightingClip Tilt(double start, double length, double from, double to) =>
        new() { Start = S(start), Length = S(length), Tilt = new TiltMove { StartDegrees = from, EndDegrees = to } };

    [Fact]
    public void An_angle_is_a_position_in_the_fixture_s_travel_with_0_degrees_in_the_middle()
    {
        var sixteen = new TiltChannel(230, IsFine: false, IsSixteenBit: true);

        Assert.Equal(0, sixteen.Position(-115));
        Assert.Equal(ushort.MaxValue, sixteen.Position(115));
        Assert.Equal(ushort.MaxValue, sixteen.Position(400));   // past the end: the end
        Assert.Equal(128, new TiltChannel(230, false, false).ValueAt(0));
        Assert.Equal(0, new TiltChannel(null, false, false).ValueAt(-90));   // travel not known: ±90°
    }

    [Fact]
    public void A_16_bit_tilt_moves_both_bytes_together_smoothly()
    {
        var bar = Patch("14 channel");
        var track = new TimelineTrack
        {
            Kind = TrackKind.Lighting,
            Channels = [Lane(LaneTarget.FixtureControl(bar.Id, ChannelFunction.Tilt)), Lane(LaneTarget.FixtureChannel(bar.Id, 2))],
            Events = [Tilt(0, 10, 0, 10)],
        };
        var compiled = Compile(Timeline(track), Context([bar]));
        var tilt = new TiltChannel(230, false, true);

        int Wide(double seconds) { var (values, _) = LevelsAt(compiled, seconds); return values[0] << 8 | values[1]; }

        Assert.Equal(tilt.Position(0), Wide(0));
        Assert.Equal(tilt.Position(10), Wide(10));
        Assert.InRange(Wide(5), tilt.Position(5) - 1, tilt.Position(5) + 1);   // half way: half the angle, in both bytes
        Assert.True(Wide(5.01) > Wide(5));   // between coarse steps, the fine byte keeps it moving
        Assert.Equal(tilt.Position(10), Wide(30));   // after the clip, it holds
    }

    [Fact]
    public void A_tilt_without_a_fine_channel_moves_its_one_byte_and_other_channels_hold()
    {
        var bar = Patch("5 channel");
        var red = Lane(LaneTarget.FixtureControl(bar.Id, ChannelFunction.Red));
        red.Start = ChannelStart.Fixed;
        red.StartLevel = 200;
        var track = new TimelineTrack
        {
            Kind = TrackKind.Lighting,
            Channels = [Lane(LaneTarget.FixtureControl(bar.Id, ChannelFunction.Tilt)), red],
            Events = [Tilt(0, 4, -115, 115)],
        };
        var compiled = Compile(Timeline(track), Context([bar]));

        Assert.Equal((0, 200), (LevelsAt(compiled, 0).Values[0], LevelsAt(compiled, 0).Values[1]));
        Assert.Equal((128, 200), (LevelsAt(compiled, 2).Values[0], LevelsAt(compiled, 2).Values[1]));
        Assert.Equal(255, LevelsAt(compiled, 4).Values[0]);
    }

    /// <summary>The 7 channel mode: tilt (one byte) and a dimmer, lit at full from the start.</summary>
    static TimelineTrack Lit(PatchedFixture bar, params LightingClip[] clips)
    {
        var dimmer = Lane(LaneTarget.FixtureControl(bar.Id, ChannelFunction.Intensity));
        dimmer.Start = ChannelStart.Fixed;
        dimmer.StartLevel = 255;
        var track = new TimelineTrack
        {
            Kind = TrackKind.Lighting,
            Channels = [Lane(LaneTarget.FixtureControl(bar.Id, ChannelFunction.Tilt)), dimmer],
            Events = [.. clips],
        };
        return track;
    }

    [Fact]
    public void A_dark_move_turns_the_light_off_while_it_moves_and_a_home_move_goes_to_the_rest_position_in_the_dark()
    {
        var dark = Tilt(0, 4, -115, 115);
        dark.Tilt!.Dark = true;
        var home = new LightingClip { Start = S(6), Length = S(2), Tilt = new TiltMove { Home = true, StartDegrees = 90, EndDegrees = 90 } };
        var bar = Patch("7 channel");
        var track = Lit(bar, dark, home);
        var compiled = Compile(Timeline(track), Context([bar]));

        (int Tilt, int Dimmer) At(double seconds) => (LevelsAt(compiled, seconds).Values[0], LevelsAt(compiled, seconds).Values[1]);

        Assert.Equal((128, 0), At(2));     // moving, in the dark
        Assert.Equal((255, 255), At(5));   // there, and lit again
        Assert.Equal((128, 0), At(7));     // home: the profile's rest value (its angles aren't used), in the dark
        Assert.Equal((128, 255), At(9));
    }

    [Fact]
    public void Moving_heads_move_in_black_to_where_the_next_tilt_clip_starts_as_soon_as_they_are_dark()
    {
        LightingClip FadeOut(LaneTarget dimmer) => new()
        {
            Start = S(0), Length = S(2),
            Lanes = [new ChannelLane { Target = dimmer, Points = [new CurvePoint { Time = S(2), Value = 0, Curve = CurveKind.Linear }] }],
        };
        var bar = Patch("7 channel");
        var dimmerTarget = LaneTarget.FixtureControl(bar.Id, ChannelFunction.Intensity);
        var track = Lit(bar, FadeOut(dimmerTarget), Tilt(5, 2, 90, 0));
        var tilt = new TiltChannel(230, false, false);

        byte TiltAt(TimelineTrack t, double seconds) => LevelsAt(Compile(Timeline(t), Context([bar])), seconds).Values[0];

        Assert.Equal(tilt.ValueAt(90), TiltAt(track, 3));   // dark from 2 s: already where the move starts

        track.MoveInBlack = false;
        Assert.NotEqual(tilt.ValueAt(90), TiltAt(track, 3));   // off: it swings there as the move begins
        Assert.Equal(tilt.ValueAt(90), TiltAt(track, 5));

        track.MoveInBlack = true;
        ((LightingClip)track.Events[0]).Lanes[0].Points[0].Value = 100;   // still lit before the move: it waits
        Assert.NotEqual(tilt.ValueAt(90), TiltAt(track, 3));
    }

    [Fact]
    public void A_tilt_s_home_is_its_profile_default_or_the_middle_if_the_profile_does_not_say()
    {
        Assert.Equal(128, TiltChannel.Of(Bar.FindMode("5 channel")!, 1)!.Value.RestValue);
        var unsaid = new FixtureMode { Name = "2", Channels = [new FixtureChannel { Name = "Tilt", Function = ChannelFunction.Tilt }] };
        Assert.Equal(128, TiltChannel.Of(unsaid, 1)!.Value.RestValue);
    }

    [Fact]
    public void A_tilt_clip_s_move_is_saved_with_it_and_kept_by_a_copy()
    {
        var clip = Tilt(1, 2, -30, 45);

        var copy = (LightingClip)clip.Clone();
        copy.Tilt!.EndDegrees = 0;

        Assert.Equal((-30.0, 45.0, "Tilt", true), (clip.Tilt!.StartDegrees, clip.Tilt.EndDegrees, clip.KindName, clip.IsGenerated));
        Assert.Null(clip.SplitAt(S(1), _ => 0));   // a move is made whole, like an effect
    }
}
