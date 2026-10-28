using GhostShow.Core.Fixtures;
using GhostShow.Core.Timeline;
using static GhostShow.Tests.Timelines.TimelineTestKit;

namespace GhostShow.Tests.Timelines;

public class LightingEffectsTests
{
    static readonly LaneTarget Ch10 = LaneTarget.Raw(1, 10);

    static ChannelLane Channel(LaneTarget target, byte start = 0, ChannelStart from = ChannelStart.FirstClip) =>
        new() { Target = target, StartLevel = start, Start = from };

    static LightingClip Effect(EffectKind kind, double start, double length, Action<LightingEffect>? setup = null)
    {
        var effect = LightingEffect.Create(kind, seed: 7);
        setup?.Invoke(effect);
        return new LightingClip { Start = S(start), Length = S(length), Effect = effect };
    }

    static LightingClip Hold(double start, double length, LaneTarget target, byte level) => new()
    {
        Start = S(start), Length = S(length),
        Lanes = [new ChannelLane { Target = target, Points = [new CurvePoint { Time = S(0.01), Value = level, Curve = CurveKind.Step }, new CurvePoint { Time = S(length), Value = level }] }],
    };

    static TimelineTrack Track(IEnumerable<ChannelLane> channels, params LightingClip[] clips) =>
        new() { Kind = TrackKind.Lighting, Channels = [.. channels], Events = [.. clips] };

    /// <summary>Every level a channel takes during a stretch, sampled every 5 ms.</summary>
    static List<byte> Sample(CompiledTimeline compiled, double from, double to, int address)
    {
        var levels = new List<byte>();
        for (double t = from; t < to; t += 0.005) levels.Add(LevelsAt(compiled, t).Values[address - 1]);
        return levels;
    }

    [Fact]
    public void Channels_get_roles_from_what_they_do_and_whether_their_fixture_has_a_dimmer_on_the_track()
    {
        var withDimmer = Guid.NewGuid();
        var withoutDimmer = Guid.NewGuid();
        var single = Guid.NewGuid();
        var channels = new[]
        {
            LaneTarget.Raw(1, 1),
            LaneTarget.FixtureControl(withDimmer, ChannelFunction.Intensity),
            LaneTarget.FixtureControl(withDimmer, ChannelFunction.Red),
            LaneTarget.FixtureControl(withDimmer, ChannelFunction.Pan),
            LaneTarget.FixtureControl(withoutDimmer, ChannelFunction.Blue),
            LaneTarget.FixtureChannel(withoutDimmer, 7),   // by number: what it does comes from the patch
            LaneTarget.FixtureChannel(single, 1),          // a fixture's only (unknown) channel: a dimmer pack
        }.Select(t => Channel(t)).ToList();
        ChannelFunction? FunctionOf(LaneTarget t) => t.Channel == 7 ? ChannelFunction.Green : null;

        var roles = LightingEffects.Roles(channels, FunctionOf);

        Assert.Equal([EffectRole.Brightness, EffectRole.Brightness, EffectRole.Colour, EffectRole.Untouched,
                      EffectRole.ScaledColour, EffectRole.ScaledColour, EffectRole.Brightness], roles);
    }

    [Theory]
    [InlineData(EffectKind.Lightning)]
    [InlineData(EffectKind.BrownOut)]
    [InlineData(EffectKind.Explosion)]
    [InlineData(EffectKind.Flicker)]
    [InlineData(EffectKind.Fire)]
    [InlineData(EffectKind.Strobe)]
    [InlineData(EffectKind.Heartbeat)]
    [InlineData(EffectKind.Police)]
    public void Every_effect_starts_and_ends_where_the_lights_were_and_plays_the_same_every_time(EffectKind kind)
    {
        var effect = LightingEffect.Create(kind, seed: 3);
        var keys = LightingEffects.Keys(effect, S(6));

        Assert.True(keys.Count > 3, "it does something");
        Assert.Equal(keys.Select(k => k.Time).Order(), keys.Select(k => k.Time));
        Assert.Equal(6, keys[^1].Time);
        Assert.Equal((1.0, 0.0), (keys[^1].Dim, keys[^1].Mix));   // back to before
        Assert.Equal(keys, LightingEffects.Keys(LightingEffect.Create(kind, seed: 3), S(6)));

        var envelope = LightingEffects.EnvelopeFor(keys, EffectRole.Brightness, null, 120, S(6));
        Assert.Equal((TimeSpan.Zero, (byte)120), (envelope[0].Time, envelope[0].Value));
        Assert.Equal((S(6), (byte)120), (envelope[^1].Time, envelope[^1].Value));
        Assert.Contains(envelope, p => p.Value != 120);
    }

    [Theory]
    [InlineData(EffectKind.BrownOut)]
    [InlineData(EffectKind.Explosion)]
    [InlineData(EffectKind.Lightning)]
    public void Ending_dark_leaves_the_lights_out(EffectKind kind)
    {
        var effect = LightingEffect.Create(kind, seed: 3);
        effect.Ending = EffectEnding.Dark;

        var envelope = LightingEffects.EnvelopeFor(LightingEffects.Keys(effect, S(6)), EffectRole.Brightness, null, 200, S(6));

        Assert.Equal(0, envelope[^1].Value);
    }

    [Fact]
    public void A_different_seed_strikes_at_different_times()
    {
        var a = LightingEffects.Keys(LightingEffect.Create(EffectKind.Lightning, seed: 1), S(6));
        var b = LightingEffects.Keys(LightingEffect.Create(EffectKind.Lightning, seed: 2), S(6));

        Assert.NotEqual(a.Select(k => k.Time), b.Select(k => k.Time));
    }

    [Fact]
    public void Lightning_flashes_the_dimmer_to_full_and_the_colour_channels_to_its_colour_then_puts_them_back()
    {
        var par = TestProfiles.Fixture("Par", startAddress: 20);   // dimmer 20, red 21, green 22, blue 23, white 24
        var channels = new[] { ChannelFunction.Intensity, ChannelFunction.Red, ChannelFunction.Blue }
            .Select(f => Channel(LaneTarget.FixtureControl(par.Id, f))).ToList();
        var lightning = Effect(EffectKind.Lightning, 2, 4, e => e.Color = "#806040");
        var before = Hold(0, 1, channels[0].Target, 40);
        before.Lanes.Add(Hold(0, 1, channels[1].Target, 10).Lanes[0]);
        var track = Track(channels, before, lightning);

        var compiled = Compile(Timeline(track), Context(TestProfiles.Patch(par).Fixtures));

        Assert.Equal(255, Sample(compiled, 2, 6, 20).Max());
        Assert.Equal(0x80, Sample(compiled, 2, 6, 21).Max());   // red: the colour's red, not full (the dimmer does the brightness)
        Assert.Equal(0x40, Sample(compiled, 2, 6, 23).Max());
        Assert.Equal((40, 10, 0), (LevelsAt(compiled, 7).Values[19], LevelsAt(compiled, 7).Values[20], LevelsAt(compiled, 7).Values[22]));
    }

    [Fact]
    public void Without_a_dimmer_on_the_track_the_colour_channels_carry_the_brightness()
    {
        var par = TestProfiles.Fixture("Par", startAddress: 20);
        var red = Channel(LaneTarget.FixtureControl(par.Id, ChannelFunction.Red), start: 200, ChannelStart.Fixed);
        var brownOut = Effect(EffectKind.BrownOut, 0, 6, e => e.Strength = 0.75);

        var compiled = Compile(Timeline(Track([red], brownOut)), Context(TestProfiles.Patch(par).Fixtures));

        var levels = Sample(compiled, 1.5, 4.5, 21);
        Assert.InRange(levels.Average(l => (double)l), 30, 110);   // sagged to about a quarter of 200, fluttering
        Assert.True(levels.Min() < 15, "it drops out now and then");
        Assert.Equal(200, LevelsAt(compiled, 6.5).Values[20]);   // and recovers
    }

    [Fact]
    public void An_effect_starts_from_where_the_previous_clip_left_the_channel_and_the_next_carries_on_from_its_end()
    {
        var channel = Channel(Ch10);
        var fire = Effect(EffectKind.Fire, 2, 4);
        fire.Effect!.Ending = EffectEnding.Dark;
        var after = new LightingClip { Start = S(7), Length = S(2) };   // no handles: holds where it starts
        var track = Track([channel], Hold(0, 1, Ch10, 90), fire, after);

        var compiled = Compile(Timeline(track));
        var levels = TrackLevels.ResolveAll(track);

        Assert.Equal(90, levels[0].Find(fire)!.StartLevel);
        Assert.Equal(0, levels[0].Find(after)!.StartLevel);
        Assert.Equal(0, LevelsAt(compiled, 8).Values[9]);
        Assert.True(Sample(compiled, 2.5, 5.5, 10).Distinct().Count() > 10, "the flame flickers");
    }

    [Fact]
    public void Strobe_flashes_at_its_rate_in_the_dark_with_flashes_long_enough_for_dmx_to_see()
    {
        var channel = Channel(Ch10, start: 100, ChannelStart.Fixed);
        var strobe = Effect(EffectKind.Strobe, 0, 2, e => e.Rate = 5);

        var levels = Sample(Compile(Timeline(Track([channel], strobe))), 0.02, 1.99, 10);

        int flashes = levels.Zip(levels.Skip(1)).Count(p => p.First == 0 && p.Second == 255);
        Assert.InRange(flashes, 9, 10);
        Assert.Equal([(byte)0, (byte)255], levels.Distinct().Order());
        Assert.True(levels.Count(l => l == 255) * 0.005 / flashes >= 0.04, "each flash lasts at least 40 ms");
    }

    [Fact]
    public void Untouched_channels_hold_and_splitting_leaves_an_effect_whole()
    {
        var spot = TestProfiles.Fixture("Spot", startAddress: 30, profileId: "test/spot", mode: "3ch");
        var gobo = Channel(LaneTarget.FixtureControl(spot.Id, ChannelFunction.Gobo), start: 64, ChannelStart.Fixed);
        var strobe = Effect(EffectKind.Strobe, 0, 2);

        Assert.Equal([(byte)64], Sample(Compile(Timeline(Track([gobo], strobe)), Context(TestProfiles.Patch(spot).Fixtures)), 0, 2, 32).Distinct());
        Assert.Null(strobe.SplitAt(S(1), _ => 0));
        var copy = (LightingClip)strobe.Clone();
        Assert.Equal((EffectKind.Strobe, strobe.Effect!.Seed), (copy.Effect!.Kind, copy.Effect.Seed));
        Assert.NotSame(strobe.Effect, copy.Effect);
    }

    [Fact]
    public void A_long_fast_effect_stops_at_the_key_limit()
    {
        var keys = LightingEffects.Keys(new LightingEffect { Kind = EffectKind.Strobe, Rate = 25 }, TimeSpan.FromHours(1));

        Assert.InRange(keys.Count, LightingEffects.MaxKeys, LightingEffects.MaxKeys + 1);
    }
}
