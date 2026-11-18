using GhostShow.Core.Timeline;
using static GhostShow.Tests.Timelines.TimelineTestKit;

namespace GhostShow.Tests.Timelines;

/// <summary>
/// Clips whose envelopes are made from their settings (effects, looks, tilt moves) all go through
/// <see cref="IClipGenerator"/>: the track's levels are what the generator makes, whatever the kind.
/// </summary>
public class ClipGeneratorTests
{
    [Fact]
    public void Each_kind_of_clip_has_its_generator_which_names_it_and_says_whether_it_moves_heads()
    {
        var levelControl = new LightingClip();
        var lightning = new LightingClip { Effect = LightingEffect.Create(EffectKind.Lightning, 1) };
        var look = new LightingClip { Effect = LightingEffect.Create(EffectKind.Look, 1) };
        var tilt = new LightingClip { Tilt = new TiltMove { StartDegrees = -30, EndDegrees = 30 } };

        Assert.Null(levelControl.Generator);
        Assert.False(levelControl.IsGenerated);
        Assert.Equal(LightingClip.DefaultName, levelControl.KindName);
        Assert.Same(lightning.Effect, lightning.Generator);
        Assert.Equal(("Lightning", false), (lightning.KindName, lightning.Generator!.MovesHeads));
        Assert.Equal(("Look", false), (look.KindName, look.Generator!.MovesHeads));
        Assert.Same(tilt.Tilt, tilt.Generator);
        Assert.Equal((LightingClip.TiltName, true), (tilt.KindName, tilt.Generator!.MovesHeads));
    }

    [Fact]
    public void A_track_s_levels_are_what_each_clip_s_generator_makes_starting_where_the_clip_before_left_off()
    {
        var look = new LightingClip { Start = S(0), Length = S(2), Effect = LightingEffect.Create(EffectKind.Look, 1) };
        look.Effect!.Strength = 0.5;
        var strobe = new LightingClip { Start = S(3), Length = S(2), Effect = LightingEffect.Create(EffectKind.Strobe, 7) };
        var dimmer = new ChannelLane { Target = LaneTarget.Raw(1, 1) };
        var track = Lights(look, strobe);
        track.Channels.Add(dimmer);

        var levels = TrackLevels.Resolve(track, dimmer);
        var brightness = new GeneratedChannel(EffectRole.Brightness);
        var lookMade = look.Generator!.Generate(look.Length).For(brightness, 0);
        byte afterLook = levels.Clips[0].EndLevel;
        var strobeMade = strobe.Generator!.Generate(strobe.Length).For(brightness, afterLook);

        Assert.Equal(lookMade.Select(p => (p.Time, p.Value)), levels.Clips[0].Handles.Select(p => (p.Time, p.Value)));
        Assert.Equal(128, afterLook);   // the look stays on, at half
        Assert.Equal(strobeMade.Select(p => (p.Time, p.Value)), levels.Clips[1].Handles.Select(p => (p.Time, p.Value)));
        Assert.Null(levels.Clips[0].Fade);
    }

    [Fact]
    public void A_tilt_move_plays_a_16_bit_tilt_as_one_fade_and_leaves_other_channels_to_their_envelopes()
    {
        var made = ((IClipGenerator)new TiltMove { StartDegrees = -115, EndDegrees = 115 }).Generate(S(4));
        var coarse = new GeneratedChannel(EffectRole.Untouched, Tilt: new TiltChannel(230, IsFine: false, IsSixteenBit: true));
        var fine = coarse with { Tilt = new TiltChannel(230, IsFine: true, IsSixteenBit: true) };
        var eightBit = coarse with { Tilt = new TiltChannel(230, IsFine: false, IsSixteenBit: false) };

        Assert.Equal(new SixteenBitFade(0, ushort.MaxValue, Low: false), made.FadeOf(coarse));
        Assert.Equal(new SixteenBitFade(0, ushort.MaxValue, Low: true), made.FadeOf(fine));
        Assert.Null(made.FadeOf(eightBit));
        Assert.Null(made.FadeOf(new GeneratedChannel(EffectRole.Brightness)));
        Assert.Equal([0, 255], made.For(eightBit, 77).Select(p => p.Value));
    }
}
