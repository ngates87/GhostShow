using System.Text.Json.Serialization;
using GhostShow.Core.Fixtures;

namespace GhostShow.Core.Timeline;

/// <summary>
/// A tilt clip's move: the moving heads on its track tilt from <see cref="StartDegrees"/> to <see cref="EndDegrees"/>
/// over the clip, so where it's placed and how long it is say when and how fast. Angles are from straight ahead (0°,
/// the middle of the tilt's travel): negative one way, positive the other. Only tilt channels move; the track's other
/// channels hold where the clip before left them.
/// </summary>
public sealed class TiltMove : IClipGenerator
{
    public double StartDegrees { get; set; }
    public double EndDegrees { get; set; }

    /// <summary>
    /// Moves with the light off: the track's brightness (its dimmers, or the colours of a fixture without one) is at 0
    /// for the clip, and back where it was at its end, so the audience doesn't see the move.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Dark { get; set; }

    /// <summary>
    /// Goes home rather than between angles: each moving head to its tilt's rest position (its profile's default),
    /// in the dark. The angles aren't used.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Home { get; set; }

    /// <summary>Whether the light is off while it moves (a home move always is).</summary>
    [JsonIgnore]
    public bool IsDark => Dark || Home;

    public TiltMove Clone() => (TiltMove)MemberwiseClone();

    string IClipGenerator.KindName => LightingClip.TiltName;
    bool IClipGenerator.MovesHeads => true;
    IClipEnvelopes IClipGenerator.Generate(TimeSpan length) => new Envelopes(this, length);

    /// <summary>
    /// A tilt channel moves from the start angle to the end one (the move says where it starts, rather than carrying
    /// on from the clip before), or goes home. In the dark, what carries brightness is off until the clip's end, then
    /// back where it was. Every other channel holds.
    /// </summary>
    sealed class Envelopes(TiltMove move, TimeSpan length) : IClipEnvelopes
    {
        public IReadOnlyList<CurvePoint> For(GeneratedChannel channel, byte startLevel)
        {
            if (channel.Tilt is { } t)
                return
                [
                    new() { Time = TimeSpan.Zero, Value = move.Home ? t.RestValue : t.ValueAt(move.StartDegrees) },
                    // A fine byte means nothing on its own (it turns over as the coarse one steps): drawn flat, then
                    // its end value, which is where the next clip carries on from. Playback moves both bytes as one.
                    new() { Time = length, Value = move.Home ? t.RestValue : t.ValueAt(move.EndDegrees), Curve = t.IsFine ? CurveKind.Step : CurveKind.Linear },
                ];
            if (move.IsDark && channel.CarriesBrightness)
                return [new() { Time = TimeSpan.Zero, Value = 0 }, new() { Time = length, Value = startLevel, Curve = CurveKind.Step }];
            return Envelope.WithStart(null, startLevel, length);
        }

        public SixteenBitFade? FadeOf(GeneratedChannel channel) => channel.Tilt is { IsSixteenBit: true } t
            ? new SixteenBitFade(move.Home ? t.RestPosition : t.Position(move.StartDegrees), move.Home ? t.RestPosition : t.Position(move.EndDegrees), t.IsFine)
            : null;
    }
}

/// <summary>
/// A tilt channel, as a tilt clip drives it: how far its fixture tilts (<see cref="FixtureChannel.RangeDegrees"/>),
/// whether it's the fine (low) byte, whether its fixture's mode has both bytes (16-bit) or this one only, and its
/// rest (home) position as 16 bits.
/// </summary>
public readonly record struct TiltChannel(double? RangeDegrees, bool IsFine, bool IsSixteenBit, ushort RestPosition = 0x8000)
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
        return ByteOf(Position(degrees));
    }

    /// <summary>This channel's byte at home: its fixture's rest position.</summary>
    public byte RestValue => IsSixteenBit ? ByteOf(RestPosition) : (byte)(RestPosition >> 8);

    byte ByteOf(ushort position) => IsFine ? (byte)(position & 0xFF) : (byte)(position >> 8);

    /// <summary>A channel of a patched fixture as a tilt channel, or null if it isn't one.</summary>
    public static TiltChannel? Of(FixtureMode mode, int channelNumber)
    {
        if (channelNumber < 1 || channelNumber > mode.Channels.Count) return null;
        var channel = mode.Channels[channelNumber - 1];
        if (channel.Function is not (ChannelFunction.Tilt or ChannelFunction.TiltFine)) return null;
        bool sixteenBit = mode.Channels.Any(c => c.Function == ChannelFunction.TiltFine);
        var coarse = mode.Channels.FirstOrDefault(c => c.Function == ChannelFunction.Tilt);
        double? range = channel.RangeDegrees ?? coarse?.RangeDegrees;
        // Home: the profile's default for the tilt (and its fine byte). A tilt that doesn't say (0, the usual when a
        // profile leaves it out) rests in the middle, straight ahead, rather than at one end.
        ushort rest = coarse is { DefaultValue: > 0 } c
            ? (ushort)(c.DefaultValue << 8 | (mode.Channels.FirstOrDefault(f => f.Function == ChannelFunction.TiltFine)?.DefaultValue ?? 0))
            : (ushort)0x8000;
        return new TiltChannel(range, channel.Function == ChannelFunction.TiltFine, sixteenBit, rest);
    }
}
