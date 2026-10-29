using System.Text.Json.Serialization;
using GhostShow.Core.Fixtures;

namespace GhostShow.Core.Timeline;

/// <summary>The built-in effects a lighting clip can be instead of a Level Control (see <see cref="LightingEffect"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EffectKind>))]
public enum EffectKind { Lightning, BrownOut, Explosion, Flicker, Fire, Strobe, Heartbeat, Police }

/// <summary>Where an effect leaves the lights when it ends.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EffectEnding>))]
public enum EffectEnding
{
    /// <summary>Back where they were before it (where the previous clip left them).</summary>
    BackToBefore,

    /// <summary>Out: brightness (and colour on fixtures without a dimmer) at 0.</summary>
    Dark,
}

/// <summary>
/// A built-in effect (VenueMagic's built-in fixture effects): a lighting clip with one of these plays generated
/// envelopes instead of handles of its own. They're made from a few settings, for every channel of the track, starting
/// where the previous clip left each channel (<see cref="LightingEffects"/>), so they fit in among Level Control clips.
/// Random parts (when lightning strikes, how a flame flickers) come from <see cref="Seed"/>: the same settings always
/// play the same way.
/// </summary>
public sealed class LightingEffect
{
    public EffectKind Kind { get; set; }

    /// <summary>0-1: how bright the flashes are, or how deep the dimming goes (<see cref="EffectInfo.StrengthLabel"/>).</summary>
    public double Strength { get; set; } = 1;

    /// <summary>How fast or how many, per kind (<see cref="EffectInfo.RateLabel"/>); unused by some.</summary>
    public double Rate { get; set; }

    /// <summary>Flash or glow colour, e.g. "#FF6A10"; null for the kind's own. Unused by kinds without one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; set; }

    public int Seed { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public EffectEnding Ending { get; set; }

    public LightingEffect Clone() => (LightingEffect)MemberwiseClone();

    public static LightingEffect Create(EffectKind kind, int seed)
    {
        var info = EffectInfo.Of(kind);
        return new() { Kind = kind, Strength = info.DefaultStrength, Rate = info.DefaultRate, Seed = seed };
    }
}

/// <summary>What the editor shows for an effect kind, and its defaults.</summary>
/// <param name="RateLabel">What <see cref="LightingEffect.Rate"/> means for this kind; null if it has no rate.</param>
/// <param name="DefaultColor">The flash or glow colour; null if the kind has no colour setting.</param>
/// <param name="DefaultStrength">Where <see cref="LightingEffect.Strength"/> starts (a flame flickering all the way to black looks like a fault).</param>
public sealed record EffectInfo(EffectKind Kind, string Name, string Description, TimeSpan DefaultLength, string StrengthLabel,
                                string? RateLabel, double DefaultRate, double MinRate, double MaxRate, string? DefaultColor,
                                double DefaultStrength = 1)
{
    public static IReadOnlyList<EffectInfo> All { get; } =
    [
        new(EffectKind.Lightning, "Lightning", "Strikes of cold flickering flashes, then back to how it was.",
            TimeSpan.FromSeconds(4), "Brightness", "Strikes", 2, 1, 10, "#DCE6FF"),
        new(EffectKind.BrownOut, "Brown out", "The power sags: the lights dim, stutter and drop out, then recover.",
            TimeSpan.FromSeconds(6), "Depth", "Flutter (per second)", 6, 1, 20, null, 0.75),
        new(EffectKind.Explosion, "Explosion", "A blinding flash, then a fireball glow that dies away in the dark.",
            TimeSpan.FromSeconds(5), "Brightness", null, 0, 0, 0, "#FF5A00"),
        new(EffectKind.Flicker, "Failing bulb", "Mostly on, with sudden stuttering drop-outs.",
            TimeSpan.FromSeconds(5), "Depth", "Flickers (per second)", 8, 1, 30, null, 0.9),
        new(EffectKind.Fire, "Fire", "A warm, restless flicker, like flames or a torch.",
            TimeSpan.FromSeconds(8), "Flicker depth", "Speed", 6, 1, 20, "#FF6A10", 0.6),
        new(EffectKind.Strobe, "Strobe", "Hard flashes in the dark.",
            TimeSpan.FromSeconds(3), "Brightness", "Flashes (per second)", 10, 1, 25, "#FFFFFF"),
        new(EffectKind.Heartbeat, "Heartbeat", "A lub-dub pulse of colour over the room.",
            TimeSpan.FromSeconds(6), "Brightness", "Beats (per minute)", 70, 30, 200, "#FF0000"),
        new(EffectKind.Police, "Police lights", "Red and blue double flashes, taking turns.",
            TimeSpan.FromSeconds(6), "Brightness", "Cycles (per second)", 1.5, 0.5, 6, null),
    ];

    public static EffectInfo Of(EffectKind kind) => All.First(i => i.Kind == kind);
}

/// <summary>How an effect treats a track channel, from what the channel does.</summary>
public enum EffectRole
{
    /// <summary>A dimmer (or a raw DMX channel, or a fixture's only unknown channel): carries the brightness.</summary>
    Brightness,

    /// <summary>A colour channel of a fixture whose dimmer is on the track too: takes the colour, the dimmer the brightness.</summary>
    Colour,

    /// <summary>A colour channel of a fixture with no dimmer on the track: takes the colour and the brightness.</summary>
    ScaledColour,

    /// <summary>Anything else (pan, gobo, strobe, …): left where it was.</summary>
    Untouched,
}

/// <summary>
/// Makes effect clips' envelopes. An effect is first a list of keys (shared by every channel, so they all flash
/// together); each key says, at a time, how much of the earlier level is left (Dim), how much the effect takes over
/// (Mix), and how bright and what colour the effect is there. Each channel's level then follows from its
/// <see cref="EffectRole"/> and where it was when the clip started.
/// </summary>
public static class LightingEffects
{
    /// <summary>The most keys one clip makes (a long, fast strobe stops adding flashes here).</summary>
    public const int MaxKeys = 4000;

    public readonly record struct Rgb(double R, double G, double B)
    {
        public static readonly Rgb White = new(1, 1, 1);

        public static Rgb Parse(string? hex, Rgb fallback)
        {
            if (hex is not { Length: 7 } || hex[0] != '#' || !int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int v))
                return fallback;
            return new((v >> 16 & 255) / 255.0, (v >> 8 & 255) / 255.0, (v & 255) / 255.0);
        }

        public Rgb Lerp(Rgb to, double f) => new(R + (to.R - R) * f, G + (to.G - G) * f, B + (to.B - B) * f);
    }

    /// <param name="Time">Seconds from the clip's start.</param>
    /// <param name="Dim">How much of the earlier level is left (0-1).</param>
    /// <param name="Mix">How much the effect takes over (0: the earlier level, dimmed; 1: the effect's own).</param>
    /// <param name="Bright">The effect's own brightness (0-1).</param>
    /// <param name="Curve">How the level gets here from the previous key.</param>
    public readonly record struct Key(double Time, double Dim, double Mix, double Bright, Rgb Colour, CurveKind Curve);

    /// <summary>Each track channel's role: what its function is, and whether its fixture has a dimmer on the track.</summary>
    /// <param name="functionOf">What a fixture channel by number does (from the patch); null if not known.</param>
    public static IReadOnlyList<EffectRole> Roles(IReadOnlyList<ChannelLane> channels, Func<LaneTarget, ChannelFunction?>? functionOf)
    {
        ChannelFunction? FunctionOf(LaneTarget t) => t.Control ?? functionOf?.Invoke(t);
        var functions = channels.Select(c => c.Target.IsRaw ? (ChannelFunction?)null : FunctionOf(c.Target)).ToList();
        var dimmed = new HashSet<Guid>();
        var lit = new HashSet<Guid>();   // fixtures with a dimmer or colour channel on the track
        for (int i = 0; i < channels.Count; i++)
        {
            if (channels[i].Target.FixtureId is not { } id) continue;
            if (functions[i] == ChannelFunction.Intensity) dimmed.Add(id);
            if (functions[i] is { } f && (f == ChannelFunction.Intensity || IsColour(f))) lit.Add(id);
        }
        return channels.Select((c, i) => c.Target.FixtureId is not { } id ? EffectRole.Brightness : functions[i] switch
        {
            ChannelFunction.Intensity => EffectRole.Brightness,
            { } f when IsColour(f) => dimmed.Contains(id) ? EffectRole.Colour : EffectRole.ScaledColour,
            null or ChannelFunction.Generic when !lit.Contains(id) => EffectRole.Brightness,
            _ => EffectRole.Untouched,
        }).ToList();
    }

    /// <summary>A colour channel: red, green, blue, white, amber or UV.</summary>
    public static bool IsColour(ChannelFunction f) => f is ChannelFunction.Red or ChannelFunction.Green or ChannelFunction.Blue
        or ChannelFunction.White or ChannelFunction.Amber or ChannelFunction.UV;

    /// <summary>
    /// One channel's envelope in an effect clip, with its start handle (as <see cref="Envelope.WithStart"/>): from
    /// startLevel at 0 to the end. Untouched channels hold.
    /// </summary>
    /// <param name="function">The channel's function (for its colour component); null for a dimmer or raw channel.</param>
    public static IReadOnlyList<CurvePoint> EnvelopeFor(IReadOnlyList<Key> keys, EffectRole role, ChannelFunction? function, byte startLevel, TimeSpan length)
    {
        if (role == EffectRole.Untouched || keys.Count == 0) return Envelope.WithStart(null, startLevel, length);
        var points = new List<CurvePoint>(keys.Count + 1) { new() { Time = TimeSpan.Zero, Value = startLevel } };
        foreach (var key in keys)
        {
            if (key.Time <= 0) continue;
            double component = function is { } f ? Component(key.Colour, f) : 1;
            double level = role switch
            {
                EffectRole.Brightness => Lerp(startLevel * key.Dim, key.Bright * 255, key.Mix),
                EffectRole.ScaledColour => Lerp(startLevel * key.Dim, component * key.Bright * 255, key.Mix),
                _ => Lerp(startLevel, component * 255, key.Mix),
            };
            points.Add(new CurvePoint { Time = TimeSpan.FromSeconds(key.Time), Value = Envelope.Round(level), Curve = key.Curve });
        }
        return points;
    }

    static double Lerp(double a, double b, double f) => a + (b - a) * Math.Clamp(f, 0, 1);

    /// <summary>How much of a colour (0-1) a colour channel gives: white is what all three share, amber the orange part.</summary>
    public static double Component(Rgb c, ChannelFunction f) => f switch
    {
        ChannelFunction.Red => c.R,
        ChannelFunction.Green => c.G,
        ChannelFunction.Blue => c.B,
        ChannelFunction.White => Math.Min(c.R, Math.Min(c.G, c.B)),
        ChannelFunction.Amber => Math.Max(0, Math.Min(c.R, 2 * c.G) - c.B),
        ChannelFunction.UV => 0,
        _ => 1,
    };

    /// <summary>The effect's keys over a clip of this length, in time order, ending at the length.</summary>
    public static IReadOnlyList<Key> Keys(LightingEffect effect, TimeSpan length)
    {
        double total = length.TotalSeconds;
        if (total <= 0) return [];
        var info = EffectInfo.Of(effect.Kind);
        var keys = new KeyList(total, new Random32(effect.Seed * 31 + (int)effect.Kind));
        double strength = Math.Clamp(effect.Strength, 0, 1);
        double rate = info.RateLabel is null ? 0 : Math.Clamp(effect.Rate, info.MinRate, info.MaxRate);
        var colour = Rgb.Parse(effect.Color ?? info.DefaultColor, Rgb.White);
        bool back = effect.Ending == EffectEnding.BackToBefore;
        switch (effect.Kind)
        {
            case EffectKind.Lightning: Lightning(keys, strength, (int)Math.Round(rate), colour); break;
            case EffectKind.BrownOut: BrownOut(keys, strength, rate, back); break;
            case EffectKind.Explosion: Explosion(keys, strength, colour, back); break;
            case EffectKind.Flicker: Flicker(keys, strength, rate); break;
            case EffectKind.Fire: Fire(keys, strength, rate, colour); break;
            case EffectKind.Strobe: Strobe(keys, strength, rate, colour); break;
            case EffectKind.Heartbeat: Heartbeat(keys, strength, rate, colour); break;
            case EffectKind.Police: Police(keys, strength, rate); break;
        }
        keys.End(back);
        return keys.List;
    }

    /// <summary>Collects keys, dropping any past the end (the end key is added last).</summary>
    sealed class KeyList(double total, Random32 random)
    {
        public readonly List<Key> List = new();
        public double Total { get; } = total;
        public Random32 Random { get; } = random;

        /// <summary>Whether the key limit (<see cref="MaxKeys"/>) has been reached.</summary>
        public bool Full => List.Count >= MaxKeys;

        public void Add(double time, double dim, double mix, double bright, Rgb colour, CurveKind curve = CurveKind.Linear)
        {
            if (time < 0 || time >= Total || Full) return;
            if (List.Count > 0 && time < List[^1].Time) time = List[^1].Time;
            List.Add(new Key(time, dim, mix, bright, colour, curve));
        }

        /// <summary>The end key: back to the earlier levels, or dark.</summary>
        public void End(bool back)
        {
            var last = List.Count > 0 ? List[^1] : new Key(0, 1, 0, 0, Rgb.White, CurveKind.Linear);
            // Hard-edged effects (strobe, flicker) switch at the end; smooth ones (a flame) fade over their last moment.
            var curve = last.Curve == CurveKind.Step ? CurveKind.Step : CurveKind.Linear;
            List.Add(new Key(Total, back ? 1 : 0, 0, 0, last.Colour, curve));
        }
    }

    /// <summary>A tiny deterministic generator (xorshift), so a seed plays the same on every computer and .NET version.</summary>
    sealed class Random32(int seed)
    {
        uint _state = (uint)seed * 2654435761u | 1;

        /// <summary>0 (inclusive) to 1 (exclusive).</summary>
        public double Next()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return (_state >> 8) / 16777216.0;
        }

        public double Between(double min, double max) => min + (max - min) * Next();
    }

    // ---- the effects ----

    static void Lightning(KeyList k, double strength, int strikes, Rgb colour)
    {
        var r = k.Random;
        for (int s = 0; s < strikes; s++)
        {
            // Strikes spread over the clip (each somewhere in its own share), leaving the last part for the afterglow.
            double t = k.Total * (0.04 + 0.76 * (s + r.Between(0, 0.7)) / strikes);
            int flashes = 2 + (int)(r.Next() * 3);
            double bright = strength;
            for (int f = 0; f < flashes; f++)
            {
                double on = r.Between(0.03, 0.09);
                k.Add(t, 1, 1, bright, colour, CurveKind.Step);
                k.Add(t + on * 0.4, 1, 1, bright, colour);   // full for a moment (long enough for DMX to show it)
                k.Add(t + on, 1, 1, bright * r.Between(0.4, 0.8), colour);
                k.Add(t + on, 1, 0, 0, colour, CurveKind.Step);
                t += on + r.Between(0.04, 0.16);
                bright = strength * r.Between(0.45, 1);
            }
            // A dimmer afterglow as the strike dies away.
            k.Add(t, 1, 0.35 * strength, strength, colour, CurveKind.Step);
            k.Add(t + r.Between(0.2, 0.45), 1, 0, 0, colour, CurveKind.Spline);
        }
    }

    static void BrownOut(KeyList k, double depth, double rate, bool back)
    {
        var r = k.Random;
        double low = 1 - depth, step = 1 / rate;
        double sag = k.Total * 0.15, recover = back ? k.Total * 0.15 : 0;
        // Sagging down, unsteadily.
        int n = Math.Max(2, (int)(sag * rate));
        for (int i = 1; i <= n; i++)
            k.Add(sag * i / n, Math.Clamp(1 + (low - 1) * i / n + r.Between(-0.12, 0.12) * depth, 0, 1), 0, 0, Rgb.White);
        // Low and fluttering, with a drop-out or more (about one every 2.5 s; always at least one).
        double lowFrom = sag + step, lowTo = k.Total - recover;
        int dropCount = Math.Max(1, (int)Math.Round((lowTo - lowFrom) / 2.5));
        var drops = Enumerable.Range(0, dropCount).Select(i => lowFrom + (lowTo - lowFrom) * (i + r.Between(0.1, 0.8)) / dropCount).ToList();
        int nextDrop = 0;
        for (double t = lowFrom; t < lowTo; t += step * r.Between(0.6, 1.4))
        {
            if (nextDrop < drops.Count && t >= drops[nextDrop])
            {
                nextDrop++;
                k.Add(t, low * 0.08, 0, 0, Rgb.White, CurveKind.Step);
                t += r.Between(0.05, 0.25);
                k.Add(t, low, 0, 0, Rgb.White, CurveKind.Step);
            }
            else k.Add(t, Math.Clamp(low + r.Between(-0.1, 0.18) * depth, 0, 1), 0, 0, Rgb.White);
        }
        if (!back) return;
        // Stuttering back up.
        double at = k.Total - recover;
        for (int i = 0; i < 3; i++)
        {
            k.Add(at, 1, 0, 0, Rgb.White, CurveKind.Step);
            at += r.Between(0.04, 0.12);
            k.Add(at, low, 0, 0, Rgb.White, CurveKind.Step);
            at += r.Between(0.08, 0.2);
        }
        k.Add(Math.Min(at, k.Total - 0.01), low, 0, 0, Rgb.White, CurveKind.Step);
    }

    static void Explosion(KeyList k, double strength, Rgb fire, bool back)
    {
        var r = k.Random;
        double burnEnd = k.Total * 0.75;
        k.Add(0.01, 0, 1, strength, Rgb.White, CurveKind.Step);   // the flash
        k.Add(0.12, 0, 1, strength, Rgb.White);
        k.Add(0.25, 0, 1, strength * 0.9, fire);                  // turning into a fireball
        for (double t = 0.33; t < burnEnd; t += r.Between(0.06, 0.12))
        {
            double left = 1 - (t - 0.25) / (burnEnd - 0.25);
            k.Add(t, 0, 1, strength * Math.Pow(left, 1.5) * r.Between(0.6, 1), fire.Lerp(new Rgb(1, 0.15, 0), 1 - left));
        }
        k.Add(burnEnd, 0, 0, 0, fire, CurveKind.Spline);          // dark
        if (back) k.Add(k.Total - (k.Total - burnEnd) * 0.3, 0.2, 0, 0, fire, CurveKind.Spline);   // then slowly back
    }

    static void Flicker(KeyList k, double depth, double rate)
    {
        var r = k.Random;
        double low = 1 - depth, t = r.Between(0.1, 0.6);
        while (t < k.Total && !k.Full)
        {
            if (r.Next() < 0.4)
            {
                // A burst of stutters.
                int count = 2 + (int)(r.Next() * 5);
                for (int i = 0; i < count; i++)
                {
                    k.Add(t, Math.Clamp(low + r.Between(0, 0.25) * depth, 0, 1), 0, 0, Rgb.White, CurveKind.Step);
                    t += r.Between(0.3, 1) / rate;
                    k.Add(t, r.Next() < 0.2 ? 0.75 : 1, 0, 0, Rgb.White, CurveKind.Step);
                    t += r.Between(0.3, 1.2) / rate;
                }
            }
            t += r.Between(2, 10) / rate;   // steady for a while
        }
    }

    static void Fire(KeyList k, double depth, double rate, Rgb flame)
    {
        var r = k.Random;
        var ember = flame.Lerp(new Rgb(1, 0.12, 0), 0.6);
        double fadeIn = Math.Min(0.3, k.Total / 4);
        k.Add(fadeIn, 1, 1, 1 - depth * 0.3, flame);
        for (double t = fadeIn + 1 / rate; t < k.Total - fadeIn; t += r.Between(0.7, 1.3) / rate)
        {
            double flicker = r.Next();
            k.Add(t, 1, 1, 1 - depth * flicker, flame.Lerp(ember, flicker * 0.7), CurveKind.Spline);
        }
        k.Add(k.Total - fadeIn, 1, 1, 1 - depth * 0.5, flame, CurveKind.Spline);
    }

    static void Strobe(KeyList k, double strength, double rate, Rgb colour)
    {
        // DMX only updates about 40 times a second: a flash shorter than ~40 ms could be missed.
        double period = 1 / rate, on = Math.Min(period / 2, Math.Max(0.04, period * 0.3));
        k.Add(0.001, 0, 0, 0, colour, CurveKind.Step);   // dark straight away
        for (double t = 0.01; t < k.Total && !k.Full; t += period)
        {
            k.Add(t, 0, 1, strength, colour, CurveKind.Step);
            k.Add(t + on, 0, 0, 0, colour, CurveKind.Step);
        }
    }

    static void Heartbeat(KeyList k, double strength, double bpm, Rgb colour)
    {
        double period = 60 / bpm, scale = Math.Min(1, period / 0.8);   // fast beats squeeze the lub-dub
        for (double t = 0.05; t < k.Total && !k.Full; t += period)
        {
            k.Add(t, 1, 0, 0, colour, CurveKind.Step);
            k.Add(t + 0.07 * scale, 1, 1, strength, colour);                // lub
            k.Add(t + 0.2 * scale, 1, 0.15, strength, colour, CurveKind.Spline);
            k.Add(t + 0.29 * scale, 1, 0.75, strength, colour);             // dub
            k.Add(t + 0.55 * scale, 1, 0, 0, colour, CurveKind.Spline);
        }
    }

    static void Police(KeyList k, double strength, double rate)
    {
        Rgb red = new(1, 0, 0), blue = new(0, 0.1, 1);
        double half = 1 / rate / 2, on = Math.Min(0.08, half / 4.5);
        for (double t = 0.01; t < k.Total && !k.Full; t += half)
        {
            var colour = (int)Math.Round((t - 0.01) / half) % 2 == 0 ? red : blue;
            for (int i = 0; i < 2; i++)
            {
                double at = t + i * on * 2;
                k.Add(at, 1, 1, strength, colour, CurveKind.Step);
                k.Add(at + on, 1, 0, 0, colour, CurveKind.Step);
            }
        }
    }
}
