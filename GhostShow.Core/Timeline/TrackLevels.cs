using GhostShow.Core.Fixtures;

namespace GhostShow.Core.Timeline;

/// <summary>
/// The level along an envelope (a channel's points, in time order). Between two points the level follows the later
/// point's <see cref="CurveKind"/>: a straight fade, a hold-then-jump, or a smooth curve. Points at the same time make
/// a jump (the later one wins). Before the first point the level is the first point's; after the last, the last's.
/// </summary>
public static class Envelope
{
    /// <summary>An envelope with its start handle: startLevel at 0, then the handles (in time order). Without handles, a flat line to the end.</summary>
    public static IReadOnlyList<CurvePoint> WithStart(IReadOnlyList<CurvePoint>? handles, byte startLevel, TimeSpan length)
    {
        var result = new List<CurvePoint>((handles?.Count ?? 0) + 1) { new() { Time = TimeSpan.Zero, Value = startLevel } };
        if (handles is null || handles.Count == 0) result.Add(new CurvePoint { Time = length, Value = startLevel });
        else result.AddRange(handles);
        return result;
    }

    public static double ValueAt(IReadOnlyList<CurvePoint> points, TimeSpan time)
    {
        if (points.Count == 0) return 0;
        int next = 0;
        while (next < points.Count && points[next].Time <= time) next++;
        if (next == 0) return points[0].Value;
        if (next == points.Count) return points[^1].Value;
        return Segment(points, next, time);
    }

    /// <summary>The level at a time on the segment from points[index - 1] to points[index].</summary>
    public static double Segment(IReadOnlyList<CurvePoint> points, int index, TimeSpan time)
    {
        var (a, b) = (points[index - 1], points[index]);
        long width = (b.Time - a.Time).Ticks;
        if (width <= 0 || b.Curve == CurveKind.Step) return time >= b.Time ? b.Value : a.Value;
        double f = Math.Clamp((double)(time - a.Time).Ticks / width, 0, 1);
        if (b.Curve == CurveKind.Linear) return a.Value + (b.Value - a.Value) * f;

        // Spline: a cubic Hermite segment with monotone (Fritsch-Butland) tangents, so it never overshoots the points
        // (and so never leaves 0-255).
        double seconds = width / (double)TimeSpan.TicksPerSecond;
        double m0 = Tangent(points, index - 1) * seconds, m1 = Tangent(points, index) * seconds;
        double f2 = f * f, f3 = f2 * f;
        double value = (2 * f3 - 3 * f2 + 1) * a.Value + (f3 - 2 * f2 + f) * m0 + (-2 * f3 + 3 * f2) * b.Value + (f3 - f2) * m1;
        return Math.Clamp(value, 0, 255);
    }

    /// <summary>The level as DMX: rounded and kept within 0-255.</summary>
    public static byte Round(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

    /// <summary>
    /// A spline segment as short straight pieces (for playback, which fades linearly, and for drawing): the times and
    /// levels at the piece ends, from the segment's start to its end. Pieces are about 40 ms apart.
    /// </summary>
    public static IEnumerable<(TimeSpan Time, double Value)> SampleSegment(IReadOnlyList<CurvePoint> points, int index, int maxPieces = 64)
    {
        var (a, b) = (points[index - 1], points[index]);
        var width = b.Time - a.Time;
        int pieces = (int)Math.Clamp(Math.Ceiling(width.TotalMilliseconds / 40), 2, maxPieces);
        for (int i = 0; i <= pieces; i++)
        {
            var time = a.Time + width * i / pieces;
            yield return (time, i == pieces ? b.Value : Segment(points, index, time));
        }
    }

    /// <summary>Slope (level per second) of the segment ending at points[k]; 0 for a zero-length one.</summary>
    static double Secant(IReadOnlyList<CurvePoint> points, int k)
    {
        double seconds = (points[k].Time - points[k - 1].Time).TotalSeconds;
        return seconds > 0 ? (points[k].Value - points[k - 1].Value) / seconds : 0;
    }

    static double Tangent(IReadOnlyList<CurvePoint> points, int k)
    {
        bool hasLeft = k > 0, hasRight = k + 1 < points.Count;
        if (!hasLeft) return hasRight ? Secant(points, k + 1) : 0;
        if (!hasRight) return Secant(points, k);
        double left = Secant(points, k), right = Secant(points, k + 1);
        if (left * right <= 0) return 0;   // a peak, a valley or a flat: level there
        double h0 = (points[k].Time - points[k - 1].Time).TotalSeconds, h1 = (points[k + 1].Time - points[k].Time).TotalSeconds;
        return 3 * (h0 + h1) / ((2 * h1 + h0) / left + (h1 + 2 * h0) / right);
    }
}

/// <summary>
/// How one channel of a lighting track plays across the track's clips (the rules are on <see cref="LightingClip"/>):
/// before the first clip, per its <see cref="ChannelLane.Start"/>; then each enabled clip's envelope, starting where
/// the previous one left the channel, until it ends or the next clip starts; between clips, the level holds. The
/// compiler and the editor both use this, so what's drawn is what plays.
/// </summary>
public sealed class TrackLevels
{
    /// <summary>A clip's envelope for the channel, with its start handle resolved.</summary>
    /// <param name="Lane">The clip's envelope for the channel (Handles are its points after the start handle), or null if it has none.</param>
    /// <param name="PlaysFor">How long (from the clip's start) it plays before the next clip takes over: its length, or less.</param>
    public sealed record ClipEnvelope(LightingClip Clip, ChannelLane? Lane, IReadOnlyList<CurvePoint> Handles, TimeSpan PlaysFor)
    {
        public byte StartLevel => Handles[0].Value;

        /// <summary>Where it leaves the channel: its level when it stops playing.</summary>
        public byte EndLevel => Envelope.Round(Envelope.ValueAt(Handles, PlaysFor));
    }

    TrackLevels(ChannelLane channel, IReadOnlyList<ClipEnvelope> clips)
    {
        Channel = channel;
        Clips = clips;
    }

    public ChannelLane Channel { get; }

    /// <summary>The enabled clips in playing order (by start).</summary>
    public IReadOnlyList<ClipEnvelope> Clips { get; }

    /// <summary>A track's enabled clips in playing order: by start (equal starts keep their order on the track).</summary>
    public static IReadOnlyList<LightingClip> PlayingOrder(TimelineTrack track) =>
        track.Events.OfType<LightingClip>().Where(c => c.Enabled).OrderBy(c => c.Start).ToList();

    /// <param name="functionOf">What a fixture channel by number does (from the patch), so effects treat it right; null if not known.</param>
    public static TrackLevels Resolve(TimelineTrack track, ChannelLane channel, Func<LaneTarget, ChannelFunction?>? functionOf = null)
    {
        int index = track.Channels.IndexOf(channel);
        var role = index >= 0 ? LightingEffects.Roles(track.Channels, functionOf)[index] : EffectRole.Brightness;
        return Resolve(PlayingOrder(track), channel, role: role, function: channel.Target.Control ?? functionOf?.Invoke(channel.Target));
    }

    /// <summary>
    /// Every channel of a track (in the track's order). Finds each clip's envelopes (and makes each effect's keys)
    /// once for all channels, so a track with many channels and clips stays quick.
    /// </summary>
    /// <param name="functionOf">What a fixture channel by number does (from the patch), so effects treat it right; null if not known.</param>
    public static IReadOnlyList<TrackLevels> ResolveAll(TimelineTrack track, Func<LaneTarget, ChannelFunction?>? functionOf = null)
    {
        var clips = PlayingOrder(track);
        var lanes = clips.Select(c =>
        {
            var byTarget = new Dictionary<LaneTarget, ChannelLane>();
            foreach (var lane in c.Lanes) byTarget.TryAdd(lane.Target, lane);
            return byTarget;
        }).ToList();
        var keys = clips.Select(c => c.Effect is { } effect ? LightingEffects.Keys(effect, c.Length) : null).ToList();
        var roles = LightingEffects.Roles(track.Channels, functionOf);
        return track.Channels.Select((channel, k) => Resolve(clips, channel, i => lanes[i].GetValueOrDefault(channel.Target), i => keys[i],
                                                             roles[k], channel.Target.Control ?? functionOf?.Invoke(channel.Target))).ToList();
    }

    /// <param name="clips">The track's clips in playing order (see <see cref="PlayingOrder"/>).</param>
    /// <param name="laneOf">The envelope of clips[i] for the channel (default: looked up in the clip).</param>
    /// <param name="keysOf">An effect clip's keys (default: made from its effect).</param>
    /// <param name="role">How effect clips treat the channel (see <see cref="LightingEffects.Roles"/>).</param>
    /// <param name="function">What the channel does (for an effect's colour); null for a dimmer or raw channel.</param>
    public static TrackLevels Resolve(IReadOnlyList<LightingClip> clips, ChannelLane channel, Func<int, ChannelLane?>? laneOf = null,
                                      Func<int, IReadOnlyList<LightingEffects.Key>?>? keysOf = null, EffectRole role = EffectRole.Brightness,
                                      ChannelFunction? function = null)
    {
        var result = new List<ClipEnvelope>(clips.Count);
        byte level = channel.StartLevel;
        for (int i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            var playsFor = i + 1 < clips.Count && clips[i + 1].Start - clip.Start < clip.Length ? clips[i + 1].Start - clip.Start : clip.Length;
            ClipEnvelope envelope;
            if (clip.Effect is { } effect)
            {
                var keys = keysOf?.Invoke(i) ?? LightingEffects.Keys(effect, clip.Length);
                envelope = new ClipEnvelope(clip, null, LightingEffects.EnvelopeFor(keys, role, function, level, clip.Length), playsFor);
            }
            else
            {
                var lane = laneOf is null ? clip.LaneFor(channel.Target) : laneOf(i);
                envelope = new ClipEnvelope(clip, lane, Envelope.WithStart(lane?.Points, level, clip.Length), playsFor);
            }
            result.Add(envelope);
            level = envelope.EndLevel;
        }
        return new TrackLevels(channel, result);
    }

    public ClipEnvelope? Find(LightingClip clip)
    {
        foreach (var envelope in Clips) if (envelope.Clip == clip) return envelope;
        return null;
    }

    /// <summary>A clip's start handle level: where the previous clip leaves the channel. A disabled clip (not playing) gets the level at its start.</summary>
    public byte StartLevelOf(LightingClip clip) => Find(clip)?.StartLevel ?? ValueAt(clip.Start) ?? Channel.StartLevel;

    /// <summary>The level as played at a time on the timeline, or null where the timeline leaves the channel alone (before the first clip).</summary>
    public byte? ValueAt(TimeSpan time)
    {
        ClipEnvelope? current = null;
        foreach (var envelope in Clips)
        {
            if (envelope.Clip.Start > time) break;
            current = envelope;
        }
        if (current is null) return Channel.Start == ChannelStart.Fixed ? Channel.StartLevel : null;
        var into = time - current.Clip.Start;
        return into < current.PlaysFor ? Envelope.Round(Envelope.ValueAt(current.Handles, into)) : current.EndLevel;
    }
}
