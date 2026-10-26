using GhostShow.Core.Model;

namespace GhostShow.Tests.Timeline;

public class ChannelTrackTests
{
    [Fact]
    public void Track_without_keyframes_outputs_nothing()
    {
        Assert.Null(new ChannelTrack().Evaluate(100));
    }

    [Fact]
    public void Keyframes_stay_sorted_when_inserted_out_of_order()
    {
        var track = TrackWithThreeKeyframes();

        Assert.Equal([0, 1000, 2000], track.Keyframes.Select(k => k.TimeMs));
    }

    [Fact]
    public void Value_is_clamped_to_first_and_last_keyframe_outside_their_range()
    {
        var track = TrackWithThreeKeyframes();

        Assert.Equal(100, track.Evaluate(-5));
        Assert.Equal(200, track.Evaluate(9999));
    }

    [Fact]
    public void Linear_keyframes_interpolate_between_values()
    {
        Assert.Equal(50, TrackWithThreeKeyframes().Evaluate(500));
    }

    [Fact]
    public void Step_keyframe_holds_its_value_until_the_next_keyframe()
    {
        var track = TrackWithThreeKeyframes();
        track.SetKeyframe(1000, 10, Interpolation.Step);

        Assert.Equal(10, track.Evaluate(1999));
    }

    [Fact]
    public void Setting_a_keyframe_at_an_existing_time_replaces_it()
    {
        var track = TrackWithThreeKeyframes();
        track.SetKeyframe(1000, 10);

        Assert.Equal(3, track.Keyframes.Count);
    }

    [Fact]
    public void Keyframes_can_be_moved_and_removed()
    {
        var track = TrackWithThreeKeyframes();

        Assert.True(track.MoveKeyframe(1000, 1500));
        Assert.Equal(1500, track.Keyframes[1].TimeMs);
        Assert.True(track.RemoveKeyframe(1500));
        Assert.Equal(2, track.Keyframes.Count);
    }

    static ChannelTrack TrackWithThreeKeyframes()
    {
        var track = new ChannelTrack();
        track.SetKeyframe(1000, 0);
        track.SetKeyframe(0, 100);
        track.SetKeyframe(2000, 200, Interpolation.Step);
        return track;
    }
}
