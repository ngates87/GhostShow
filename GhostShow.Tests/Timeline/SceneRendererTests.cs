using GhostShow.Core.Engine;
using GhostShow.Core.Model;

namespace GhostShow.Tests.Timeline;

public class SceneRendererTests
{
    [Fact]
    public void Highest_value_wins_when_two_tracks_drive_the_same_channel()
    {
        var dim = new ChannelTrack { Channel = 5 };
        dim.SetKeyframe(0, 50);
        var bright = new ChannelTrack { Channel = 5 };
        bright.SetKeyframe(0, 200);
        ChannelTrack[] tracks = [dim, bright];
        var buffers = SceneRenderer.CreateBuffers(tracks);

        SceneRenderer.Render(tracks, 0, buffers);

        Assert.Equal(200, buffers[0][4]);
        Assert.Equal(0, buffers[0][0]);
    }

    [Fact]
    public void Each_audio_clip_fires_exactly_once_across_consecutive_windows()
    {
        var track = new AudioTrack();
        track.AddClip(new AudioClip(0, "a.wav"));
        track.AddClip(new AudioClip(500, "b.wav"));
        var fired = new List<string>();

        SceneRenderer.ForEachDueClip([track], -1, 0, c => fired.Add(c.File));
        SceneRenderer.ForEachDueClip([track], 0, 499, c => fired.Add(c.File));
        SceneRenderer.ForEachDueClip([track], 499, 500, c => fired.Add(c.File));

        Assert.Equal(["a.wav", "b.wav"], fired);
    }
}
