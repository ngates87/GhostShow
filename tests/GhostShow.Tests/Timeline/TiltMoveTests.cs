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
