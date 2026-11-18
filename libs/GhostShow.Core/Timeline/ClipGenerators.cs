using GhostShow.Core.Fixtures;

namespace GhostShow.Core.Timeline;

/// <summary>
/// The settings of a clip whose envelopes are made rather than drawn: an effect or look (<see cref="LightingEffect"/>)
/// or a move (<see cref="TiltMove"/>). <see cref="TrackLevels"/>, the compiler and the editor only ask it for its
/// envelopes, so a new kind of clip (a pan or position move, say) is one class plus its place on
/// <see cref="LightingClip.Generator"/>, rather than a branch in each of them.
/// </summary>
public interface IClipGenerator
{
    /// <summary>What the clip is, as its title and the inspector say: "Lightning", "Look", "Tilt"...</summary>
    string KindName { get; }

    /// <summary>
    /// Whether it moves heads: a track's move in black (<see cref="TimelineTrack.MoveInBlack"/>) puts them where it
    /// starts while the lights are out before it.
    /// </summary>
    bool MovesHeads { get; }

    /// <summary>Makes the clip's envelopes at a length: once per clip, for all of the track's channels.</summary>
    IClipEnvelopes Generate(TimeSpan length);
}

/// <summary>A generated clip's envelopes, channel by channel (see <see cref="IClipGenerator.Generate"/>).</summary>
public interface IClipEnvelopes
{
    /// <summary>A channel's envelope over the clip, from 0 to its length, starting where the clip before left it.</summary>
    IReadOnlyList<CurvePoint> For(GeneratedChannel channel, byte startLevel);

    /// <summary>
    /// A channel the clip moves as one 16-bit fade over its whole length (both bytes together, so a slow move is
    /// smooth), rather than by its envelope; null for the envelope.
    /// </summary>
    SixteenBitFade? FadeOf(GeneratedChannel channel) => null;
}

/// <summary>What a generated clip is told about one channel of its track, from the patch.</summary>
/// <param name="Role">How it carries brightness and colour (see <see cref="LightingEffects.Roles"/>).</param>
/// <param name="Function">What it does (for an effect's colour); null for a dimmer or raw channel.</param>
/// <param name="Tilt">How it tilts, if it's a moving head's tilt (or tilt fine) channel.</param>
public readonly record struct GeneratedChannel(EffectRole Role, ChannelFunction? Function = null, TiltChannel? Tilt = null)
{
    /// <summary>Whether it carries brightness, so a dark move turns it off: a dimmer, or a colour of a fixture without one.</summary>
    public bool CarriesBrightness => Role is EffectRole.Brightness or EffectRole.ScaledColour;
}

/// <summary>A 16-bit fade between two positions; <paramref name="Low"/>: this channel is the low (fine) byte.</summary>
public readonly record struct SixteenBitFade(ushort From, ushort To, bool Low);
