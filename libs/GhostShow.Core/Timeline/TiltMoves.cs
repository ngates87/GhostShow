using GhostShow.Core.Fixtures;

namespace GhostShow.Core.Timeline;

/// <summary>
/// A tilt clip's move: the moving heads on its track tilt from <see cref="StartDegrees"/> to <see cref="EndDegrees"/>
/// over the clip, so where it's placed and how long it is say when and how fast. Angles are from straight ahead (0°,
/// the middle of the tilt's travel): negative one way, positive the other. Only tilt channels move; the track's other
/// channels hold where the clip before left them.
/// </summary>
public sealed class TiltMove
{
    public double StartDegrees { get; set; }
    public double EndDegrees { get; set; }

    public TiltMove Clone() => (TiltMove)MemberwiseClone();
}

/// <summary>
/// A tilt channel, as a tilt clip drives it: how far its fixture tilts (<see cref="FixtureChannel.RangeDegrees"/>),
/// whether it's the fine (low) byte, and whether its fixture's mode has both bytes (16-bit) or this one only.
/// </summary>
public readonly record struct TiltChannel(double? RangeDegrees, bool IsFine, bool IsSixteenBit)
{
    /// <summary>The travel assumed for a fixture whose profile doesn't say: ±90°, so the arc's ends are its ends.</summary>
    public const double UnknownRange = 180;

    public double Range => RangeDegrees is > 0 and var r ? r : UnknownRange;

    /// <summary>An angle as a 16-bit position: 0° is the middle of the travel; beyond its ends, its ends.</summary>
    public ushort Position(double degrees) => (ushort)Math.Round(Math.Clamp(degrees / Range + 0.5, 0, 1) * ushort.MaxValue);

    /// <summary>This channel's byte at an angle: the high or low byte of a 16-bit position, or the one byte it has.</summary>
    public byte ValueAt(double degrees)
    {
        if (!IsSixteenBit) return (byte)Math.Round(Math.Clamp(degrees / Range + 0.5, 0, 1) * byte.MaxValue);
        ushort position = Position(degrees);
        return IsFine ? (byte)(position & 0xFF) : (byte)(position >> 8);
    }

    /// <summary>A channel of a patched fixture as a tilt channel, or null if it isn't one.</summary>
    public static TiltChannel? Of(FixtureMode mode, int channelNumber)
    {
        if (channelNumber < 1 || channelNumber > mode.Channels.Count) return null;
        var channel = mode.Channels[channelNumber - 1];
        if (channel.Function is not (ChannelFunction.Tilt or ChannelFunction.TiltFine)) return null;
        bool sixteenBit = mode.Channels.Any(c => c.Function == ChannelFunction.TiltFine);
        double? range = channel.RangeDegrees ?? mode.Channels.FirstOrDefault(c => c.Function == ChannelFunction.Tilt)?.RangeDegrees;
        return new TiltChannel(range, channel.Function == ChannelFunction.TiltFine, sixteenBit);
    }
}
