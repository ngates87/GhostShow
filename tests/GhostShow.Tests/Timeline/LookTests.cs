using GhostShow.Core.Fixtures;
using GhostShow.Core.Timeline;
using static GhostShow.Tests.Timelines.TimelineTestKit;

namespace GhostShow.Tests.Timelines;

/// <summary>
/// Look clips (a fade to a colour and brightness that stays on), and the editor's colour strip (what a track's lights
/// look like over time).
/// </summary>
public class LookTests
{
    static readonly Guid Par = Guid.NewGuid();

    static ChannelLane Channel(LaneTarget target) => new() { Target = target, StartLevel = 0, Start = ChannelStart.Fixed };

    static LightingClip Look(double start, double length, string colour, double brightness, double fade, EffectEnding? ending = null)
    {
        var effect = LightingEffect.Create(EffectKind.Look, seed: 1);
        (effect.Color, effect.Strength, effect.Rate) = (colour, brightness, fade);
        if (ending is { } e) effect.Ending = e;
        return new LightingClip { Start = S(start), Length = S(length), Effect = effect };
    }

    static TimelineTrack ParTrack(params LightingClip[] clips) => new()
    {
        Kind = TrackKind.Lighting,
        Channels = [.. new[] { ChannelFunction.Intensity, ChannelFunction.Red, ChannelFunction.Green, ChannelFunction.Pan }
            .Select(f => Channel(LaneTarget.FixtureControl(Par, f)))],
        Events = [.. clips],
    };

    [Fact]
    public void A_look_fades_to_its_colour_and_brightness_and_stays_on_for_the_next_clip()
    {
        var next = new LightingClip { Start = S(6), Length = S(2) };   // Level Control, no handles: holds
        var levels = TrackLevels.ResolveAll(ParTrack(Look(0, 4, "#FF0000", 0.5, fade: 2), next));
        (byte?, byte?, byte?) At(double seconds) => (levels[0].ValueAt(S(seconds)), levels[1].ValueAt(S(seconds)), levels[2].ValueAt(S(seconds)));

        Assert.Equal(((byte?)64, (byte?)128, (byte?)0), At(1));     // half way through the fade
        Assert.Equal(((byte?)128, (byte?)255, (byte?)0), At(3));    // there: red, at half brightness
        Assert.Equal(((byte?)128, (byte?)255, (byte?)0), At(5));    // and it stays
        Assert.Equal((byte)128, levels[0].StartLevelOf(next));       // the next clip carries on from it
        Assert.Equal((byte?)0, levels[3].ValueAt(S(3)));            // pan isn't light: left alone
    }

    [Fact]
    public void A_look_that_ends_fades_away_over_its_fade_and_a_fade_of_0_switches()
    {
        IReadOnlyList<CurvePoint> EnvelopeOf(LightingClip clip) =>
            LightingEffects.EnvelopeFor(LightingEffects.Keys(clip.Effect!, clip.Length), EffectRole.Brightness, null, 100, clip.Length);
        double At(IReadOnlyList<CurvePoint> envelope, double seconds) => Envelope.ValueAt(envelope, S(seconds));

        var back = EnvelopeOf(Look(0, 4, "#FFFFFF", 1, fade: 1, EffectEnding.BackToBefore));
        Assert.Equal(255, At(back, 2));
        Assert.InRange(At(back, 3.5), 170, 185);   // half way back
        Assert.Equal(100, At(back, 4));

        var dark = EnvelopeOf(Look(0, 4, "#FFFFFF", 1, fade: 1, EffectEnding.Dark));
        Assert.Equal(0, At(dark, 4));

        var snap = EnvelopeOf(Look(0, 4, "#FFFFFF", 1, fade: 0));
        Assert.Equal(255, At(snap, 0.01));
        Assert.Equal(255, At(snap, 4));
    }

    [Fact]
    public void Changing_a_look_s_settings_is_saved_and_a_look_is_not_random()
    {
        var info = EffectInfo.Of(EffectKind.Look);
        Assert.Equal((EffectEnding.Hold, false), (LightingEffect.Create(EffectKind.Look, 1).Ending, info.IsRandom));
        Assert.Equal(LightingEffects.Keys(LightingEffect.Create(EffectKind.Look, 1), S(4)), LightingEffects.Keys(LightingEffect.Create(EffectKind.Look, 99), S(4)));
    }

    [Fact]
    public void The_colour_strip_shows_each_fixture_s_colour_scaled_by_its_dimmer()
    {
        var dimmerOnly = Guid.NewGuid();
        var track = ParTrack(Look(0, 2, "#FFFF00", 0.5, fade: 0));
        track.Channels.Add(Channel(LaneTarget.FixtureControl(dimmerOnly, ChannelFunction.Intensity)));
        track.Channels.Add(new ChannelLane { Target = LaneTarget.Raw(1, 40) });
        var levels = TrackLevels.ResolveAll(track);

        var lights = LightPreview.LightsOf(levels, null);

        Assert.Equal([Par, dimmerOnly, null], lights.Select(l => l.FixtureId));
        Assert.Equal(3, lights[0].Channels.Count);   // dimmer, red, green; not pan
        var at = LightPreview.Sample(lights[0], [S(1)]).Single()!.Value;
        Assert.Equal((0.5, 0.5, 0.0), (Math.Round(at.R, 2), Math.Round(at.G, 2), at.B));   // yellow, half bright
        Assert.Equal(new LightingEffects.Rgb(0.5, 0.43, 0.31), Rounded(LightPreview.Sample(lights[1], [S(1)]).Single()!.Value));   // a lamp: warm white, half
        // A channel the timeline leaves alone (no clips, not held from the start): nothing to show.
        var untouched = TrackLevels.ResolveAll(new TimelineTrack { Kind = TrackKind.Lighting, Channels = [new() { Target = LaneTarget.Raw(1, 1) }] })[0];
        Assert.Null(LightPreview.Sample(new LightPreview.Light(null, [(untouched, null, EffectRole.Brightness)]), [S(1)]).Single());

        static LightingEffects.Rgb Rounded(LightingEffects.Rgb c) => new(Math.Round(c.R, 2), Math.Round(c.G, 2), Math.Round(c.B, 2));
    }

    [Fact]
    public void Sampling_a_run_of_times_gives_what_the_channel_plays_at_each()
    {
        var fire = LightingEffect.Create(EffectKind.Fire, seed: 4);
        var track = ParTrack(Look(1, 2, "#FF8800", 1, fade: 0.5), new LightingClip { Start = S(4), Length = S(5), Effect = fire });
        var levels = TrackLevels.ResolveAll(track);
        var times = Enumerable.Range(0, 500).Select(i => S(i * 0.02)).ToList();

        foreach (var channel in levels)
        {
            var sampler = new LevelSampler(channel);
            Assert.Equal(times.Select(channel.ValueAt), times.Select(sampler.At));
        }
    }
}
