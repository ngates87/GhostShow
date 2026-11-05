using GhostShow.Audio;
using GhostShow.Core.Timeline;

namespace GhostShow.Tests.Audio;

public class AudioSideModifierTests
{
    [Theory]
    [InlineData(AudioSide.Stereo, 0.6f, 0.2f)]
    [InlineData(AudioSide.Left, 0.4f, 0f)]
    [InlineData(AudioSide.Right, 0f, 0.4f)]
    public void One_side_gets_both_of_the_files_channels_and_the_other_side_gets_nothing(AudioSide side, float left, float right)
    {
        float[] samples = [0.6f, 0.2f, 0.6f, 0.2f];

        new AudioSideModifier(side).Process(samples, 2);

        Assert.Equal([left, right, left, right], samples, (a, b) => Math.Abs(a - b) < 0.0001f);
    }
}
