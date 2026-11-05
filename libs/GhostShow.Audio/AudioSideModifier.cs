using GhostShow.Core.Timeline;
using SoundFlow.Abstracts;

namespace GhostShow.Audio;

/// <summary>
/// Sends a sound to one side of a stereo output, so left and right can feed different rooms. Both of the file's channels
/// are mixed into that side: plain panning would lose whatever was only on the other channel.
/// </summary>
public sealed class AudioSideModifier(AudioSide side) : SoundModifier
{
    public override void Process(Span<float> buffer, int channels)
    {
        if (side == AudioSide.Stereo || channels != 2) return;
        int kept = side == AudioSide.Left ? 0 : 1;
        for (int i = 0; i + 1 < buffer.Length; i += 2)
        {
            float mixed = (buffer[i] + buffer[i + 1]) / 2;
            buffer[i] = buffer[i + 1] = 0;
            buffer[i + kept] = mixed;
        }
    }

    // SoundFlow's other way in, one sample at a time. Mixing needs both channels, so Process does the work.
    public override float ProcessSample(float sample, int channel) => sample;
}
