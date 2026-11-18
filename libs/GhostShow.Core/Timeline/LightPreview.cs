using GhostShow.Core.Fixtures;
using static GhostShow.Core.Timeline.LightingEffects;

namespace GhostShow.Core.Timeline;

/// <summary>
/// What the lights of a lighting track look like over time, for the editor's colour strip: each fixture on the track
/// (or raw channel) as one colour, its colour channels mixed and scaled by its dimmer. Worked out from the track's
/// resolved levels (<see cref="TrackLevels"/>), so it shows what plays.
/// </summary>
public static class LightPreview
{
    /// <summary>A light on the track: a fixture's channels that make light (a dimmer, colours), or a raw channel.</summary>
    /// <param name="FixtureId">Its fixture; null for a raw channel.</param>
    public sealed record Light(Guid? FixtureId, IReadOnlyList<(TrackLevels Levels, ChannelFunction? Function, EffectRole Role)> Channels);

    /// <summary>The light of a fixture with no colour channels on the track (a dimmer, a lamp): warm white.</summary>
    public static readonly Rgb Lamp = new(1, 0.86, 0.62);

    /// <summary>The track's lights, in the order their first channel is on the track. Channels that make no light (pan, gobo, …) aren't in any.</summary>
    public static IReadOnlyList<Light> LightsOf(IReadOnlyList<TrackLevels> levels, Func<LaneTarget, ChannelFunction?>? functionOf)
    {
        var roles = Roles(levels.Select(l => l.Channel).ToList(), functionOf);
        var lights = new List<(Guid? Id, List<(TrackLevels, ChannelFunction?, EffectRole)> Channels)>();
        for (int i = 0; i < levels.Count; i++)
        {
            if (roles[i] == EffectRole.Untouched) continue;
            var target = levels[i].Channel.Target;
            var channel = (levels[i], target.IsRaw ? null : target.Control ?? functionOf?.Invoke(target), roles[i]);
            int at = target.FixtureId is { } id ? lights.FindIndex(l => l.Id == id) : -1;
            if (at >= 0) lights[at].Channels.Add(channel);
            else lights.Add((target.FixtureId, [channel]));
        }
        return lights.Select(l => new Light(l.Id, l.Channels)).ToList();
    }

    /// <summary>
    /// A light's colour at each of a run of times (which mustn't go back): its colour times its brightness (0-1 per
    /// component); null where the timeline leaves all its channels alone (before their first clip).
    /// </summary>
    public static IEnumerable<Rgb?> Sample(Light light, IEnumerable<TimeSpan> times)
    {
        var samplers = light.Channels.Select(c => new LevelSampler(c.Levels)).ToArray();
        var values = new byte?[samplers.Length];
        foreach (var time in times)
        {
            for (int i = 0; i < samplers.Length; i++) values[i] = samplers[i].At(time);
            yield return Mix(light, values);
        }
    }

    /// <summary>A light's colour from its channels' levels (in <see cref="Light.Channels"/>' order).</summary>
    public static Rgb? Mix(Light light, IReadOnlyList<byte?> values)
    {
        if (values.All(v => v is null)) return null;
        double dimmer = 1, r = 0, g = 0, b = 0;
        bool coloured = false, dimmed = false;
        for (int i = 0; i < values.Count; i++)
        {
            double level = (values[i] ?? 0) / 255.0;
            var (_, function, role) = light.Channels[i];
            if (function is { } f && IsColour(f))
            {
                coloured = true;
                var (cr, cg, cb) = f switch
                {
                    ChannelFunction.Red => (1.0, 0.0, 0.0),
                    ChannelFunction.Green => (0.0, 1.0, 0.0),
                    ChannelFunction.Blue => (0.0, 0.0, 1.0),
                    ChannelFunction.White => (1.0, 1.0, 1.0),
                    ChannelFunction.Amber => (1.0, 0.6, 0.0),
                    _ => (0.45, 0.0, 1.0),   // UV: a violet glow
                };
                r += cr * level;
                g += cg * level;
                b += cb * level;
            }
            else if (role == EffectRole.Brightness)
            {
                // A dimmer (the brightest, if a fixture has more than one on the track).
                dimmer = dimmed ? Math.Max(dimmer, level) : level;
                dimmed = true;
            }
        }
        var colour = coloured ? new Rgb(Math.Min(1, r), Math.Min(1, g), Math.Min(1, b)) : Lamp;
        return new Rgb(colour.R * dimmer, colour.G * dimmer, colour.B * dimmer);
    }
}

/// <summary>
/// A channel's level at a run of times that don't go back, as <see cref="TrackLevels.ValueAt"/> gives it, but walking
/// the clips and handles once rather than searching for each time (an effect can have thousands of keys).
/// </summary>
public sealed class LevelSampler(TrackLevels levels)
{
    int _clip = -1, _point;
    byte _end;

    public byte? At(TimeSpan time)
    {
        var clips = levels.Clips;
        while (_clip + 1 < clips.Count && clips[_clip + 1].Clip.Start <= time)
        {
            _clip++;
            _point = 0;
            _end = clips[_clip].EndLevel;
        }
        if (_clip < 0) return levels.HoldsBeforeFirstClip ? levels.Channel.StartLevel : null;
        var current = clips[_clip];
        var into = time - current.Clip.Start;
        if (into >= current.PlaysFor) return _end;
        var handles = current.Handles;
        while (_point < handles.Count && handles[_point].Time <= into) _point++;
        if (_point == 0) return handles[0].Value;
        if (_point == handles.Count) return handles[^1].Value;
        return Envelope.Round(Envelope.Segment(handles, _point, into));
    }
}
