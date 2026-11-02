namespace GhostShow.Core.Timeline;

/// <summary>
/// Turns lighting events (<see cref="LevelsEvent"/>: "these levels, over this fade") into points on their track's
/// channels (<see cref="ChannelLane"/>), so they play exactly as before:
/// - a fade becomes a point at its start (jumping to From, or staying where the channel was) and one at its end;
/// - an instant change becomes a step;
/// - a later event takes over its channels from its start (the latest start wins): an earlier fade it cuts into ends
///   there, at the level it had reached, and the earlier event's later points are dropped.
/// Events are taken in start order (then their order on the track). Disabled events are dropped: points have no
/// on/off of their own. One difference: "fade from wherever the channel was" now looks only at the same track; it
/// used to consider every track.
/// </summary>
public static class LevelEventConversion
{
    /// <summary>
    /// Brings an older timeline's lighting up to date: lighting events become points (<see cref="Convert"/>), points
    /// on a track's channels become one clip holding them (<see cref="ToClips"/>), and clips from before envelope
    /// rules get them (<see cref="ToEnvelopes"/>). Returns whether anything changed.
    /// </summary>
    /// <param name="fromVersion">The timeline file version it was read from (0: older than timeline files).</param>
    public static bool Upgrade(ShowTimeline timeline, int fromVersion = 0)
    {
        bool lanes = Convert(timeline);
        bool clips = ToClips(timeline);
        bool envelopes = fromVersion < 4 && ToEnvelopes(timeline);
        return lanes || clips || envelopes;
    }

    /// <summary>
    /// Clips from before timeline version 4 had no start or end handles: a clip's first point was a jump at its time
    /// (the channel held where it was until then), and after its last point the level held. Here the first point
    /// becomes a step and each envelope gets its end handle, so the clip plays as before; points past the end, which
    /// never played, go.
    /// </summary>
    public static bool ToEnvelopes(ShowTimeline timeline)
    {
        bool changed = false;
        foreach (var clip in timeline.Tracks.SelectMany(t => t.Events).OfType<LightingClip>())
        {
            changed = true;
            foreach (var lane in clip.Lanes)
            {
                lane.Sort();
                lane.Points.RemoveAll(p => p.Time > clip.Length);
                if (lane.Points.Count > 0) lane.Points[0].Curve = CurveKind.Step;
            }
            clip.Lanes.RemoveAll(l => l.Points.Count == 0);
            clip.EnsureEndHandles();
        }
        return changed;
    }

    /// <summary>
    /// Moves points that sit directly on a track's channels (lighting before clips) into one clip per track,
    /// spanning the first to the last point (at least 1 s), so the track plays exactly as before.
    /// </summary>
    public static bool ToClips(ShowTimeline timeline)
    {
        bool changed = false;
        foreach (var track in timeline.Tracks)
        {
            var lanes = track.Channels.Where(l => l.Points.Count > 0).ToList();
            if (lanes.Count == 0) continue;
            changed = true;
            var start = lanes.Min(l => l.Points.Min(p => p.Time));
            var end = lanes.Max(l => l.Points.Max(p => p.Time));
            var clip = new LightingClip { Start = start, Length = end - start > TimeSpan.FromSeconds(1) ? end - start : TimeSpan.FromSeconds(1) };
            foreach (var lane in lanes)
            {
                clip.Lanes.Add(new ChannelLane { Target = lane.Target, Points = lane.Points.Select(p => new CurvePoint { Time = p.Time - start, Value = p.Value, Curve = p.Curve }).ToList() });
                lane.Points.Clear();
            }
            track.Events.Add(clip);
        }
        return changed;
    }

    /// <summary>Converts every lighting event in the timeline. Returns whether there were any.</summary>
    public static bool Convert(ShowTimeline timeline)
    {
        bool converted = false;
        foreach (var track in timeline.Tracks)
        {
            var events = track.Events.OfType<LevelsEvent>().ToList();
            if (events.Count == 0) continue;
            converted = true;
            track.Events.RemoveAll(e => e is LevelsEvent);

            foreach (var e in events.Where(e => e.Enabled).OrderBy(e => e.Start))   // OrderBy is stable: track order breaks ties
            {
                foreach (var level in e.Levels)
                {
                    var target = new LaneTarget(e.FixtureId, level.Control, level.Control is null ? level.Channel : null,
                                                e.FixtureId is null ? e.Universe : Model.Project.FirstUniverse);
                    var lane = track.Channels.FirstOrDefault(l => l.Target == target);
                    if (lane is null) track.Channels.Add(lane = new ChannelLane { Target = target });
                    Apply(lane, e.Start, e.Fade, level);
                }
            }
        }
        return converted;
    }

    static void Apply(ChannelLane lane, TimeSpan start, TimeSpan fade, Level level)
    {
        byte? current = lane.ValueAt(start);   // before dropping what this event overrides
        byte from = level.From ?? current ?? 0;
        lane.Points.RemoveAll(p => p.Time >= start);
        // Cutting into an earlier fade: it still played up to here, so end it here at the level it had reached.
        if (current is { } reached && lane.Points.Count > 0 && lane.Points[^1].Value != reached)
            lane.Points.Add(new CurvePoint { Time = start, Value = reached });
        if (fade > TimeSpan.Zero)
        {
            // Jump to where the fade starts, unless the line is already there at that moment.
            if (lane.Points.Count == 0 || lane.Points[^1].Time != start || lane.Points[^1].Value != from)
                lane.Points.Add(new CurvePoint { Time = start, Value = from, Curve = CurveKind.Step });
            lane.Points.Add(new CurvePoint { Time = start + fade, Value = level.To });
        }
        else
        {
            lane.Points.Add(new CurvePoint { Time = start, Value = level.To, Curve = CurveKind.Step });
        }
    }
}
