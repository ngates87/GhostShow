using GhostShow.Core.Dmx;
using GhostShow.Core.Fixtures;

namespace GhostShow.Core.Timeline;

/// <summary>What the compiler needs to know about the rest of the show.</summary>
/// <param name="Fixtures">The patched fixtures (from the show's current patch).</param>
/// <param name="AllFixtureIds">Every fixture in the show, patched or not, to tell "unpatched" apart from "missing".</param>
/// <param name="Universes">The universes the show defines.</param>
/// <param name="ResolveAudioPath">Turns an event's file reference into a full path (see <see cref="AudioPaths"/>).</param>
/// <param name="AudioLength">The length of an audio file at a full path, if known; null if not (yet).</param>
public sealed record TimelineContext(
    IReadOnlyDictionary<Guid, PatchedFixture> Fixtures,
    IReadOnlySet<Guid> AllFixtureIds,
    IReadOnlyCollection<int> Universes,
    Func<string, string> ResolveAudioPath,
    Func<string, TimeSpan?> AudioLength);

/// <summary>
/// Validates a timeline and flattens it for playback (<see cref="CompiledTimeline"/>). Problems are reported per event
/// and the event is left out; everything else still plays. Pure: no device or file I/O beyond what the context does.
/// </summary>
public static class TimelineCompiler
{
    public static CompiledTimeline Compile(ShowTimeline timeline, TimelineContext context)
    {
        var issues = new List<TimelineIssue>();
        var steps = new List<(LevelStep Step, int Track, int Event, int Level, byte? From)>();
        var audio = new List<AudioCue>();
        var markers = new List<CompiledMarker>();
        var universeSlots = new Dictionary<int, int>();
        long contentEnd = 0;

        CheckIds(timeline, issues);
        for (int trackIndex = 0; trackIndex < timeline.Tracks.Count; trackIndex++)
        {
            var track = timeline.Tracks[trackIndex];
            if (!track.Enabled) continue;
            for (int laneIndex = 0; laneIndex < track.Channels.Count; laneIndex++)
            {
                var lane = track.Channels[laneIndex];
                var laneSteps = CompileLane(lane, context, universeSlots, issues);
                // Lanes sort after the track's events at equal times; within a lane, later points win.
                for (int i = 0; i < laneSteps.Count; i++) steps.Add((laneSteps[i], trackIndex, track.Events.Count + laneIndex, i, laneSteps[i].From));
                if (laneSteps.Count > 0) contentEnd = Math.Max(contentEnd, lane.Points.Max(p => p.Time).Ticks);
            }
            for (int eventIndex = 0; eventIndex < track.Events.Count; eventIndex++)
            {
                var e = track.Events[eventIndex];
                if (!e.Enabled) continue;
                if (e.Start < TimeSpan.Zero)
                {
                    issues.Add(Error(e, $"'{Describe(e)}' starts before 0 ({e.Start})."));
                    continue;
                }

                switch (e)
                {
                    case LevelsEvent levels:
                        var resolved = ResolveLevels(levels, context, universeSlots, issues);
                        for (int i = 0; i < resolved.Count; i++) steps.Add((resolved[i].Step, trackIndex, eventIndex, i, resolved[i].From));
                        if (resolved.Count > 0) contentEnd = Math.Max(contentEnd, (levels.Start + levels.Fade).Ticks);
                        break;
                    case AudioEvent sound:
                        if (CompileAudio(sound, context, issues) is { } cue)
                        {
                            audio.Add(cue);
                            contentEnd = Math.Max(contentEnd, cue.End ?? cue.Start);
                        }
                        break;
                    case LightingClip clip:
                        foreach (var lane in clip.Lanes)
                        {
                            // Only channels the track controls play (a channel removed from the track keeps nothing).
                            var trackLane = track.Channels.FirstOrDefault(c => c.Target == lane.Target);
                            if (trackLane is null) continue;
                            var clipSteps = CompileLane(lane, context, universeSlots, issues, clip.Start, clip.Length, trackLane.Id);
                            for (int i = 0; i < clipSteps.Count; i++) steps.Add((clipSteps[i], trackIndex, eventIndex, clip.Lanes.IndexOf(lane) * 100_000 + i, clipSteps[i].From));
                        }
                        contentEnd = Math.Max(contentEnd, (clip.Start + clip.Length).Ticks);
                        break;
                    case MarkerEvent marker:
                        markers.Add(new CompiledMarker(marker.Id, marker.Name ?? "Marker", marker.Start.Ticks));
                        contentEnd = Math.Max(contentEnd, marker.Start.Ticks);
                        break;
                }
            }
        }

        var ordered = steps.OrderBy(s => s.Step.Start).ThenBy(s => s.Track).ThenBy(s => s.Event).ThenBy(s => s.Level).ToList();
        var finalSteps = ResolveFadeStarts(ordered);
        var universes = universeSlots.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToArray();
        long length = timeline.Length?.Ticks ?? contentEnd;

        return new CompiledTimeline(finalSteps, universes, audio.OrderBy(a => a.Start).ToArray(),
                                    markers.OrderBy(m => m.Time).ToArray(), length, timeline.Loop, issues);
    }

    /// <summary>
    /// A level without From fades from whatever the timeline had on that channel when the fade starts: the previous
    /// step's value at this step's start (part-way through, if that one was still fading), or 0 if there's none.
    /// Steps are in playback order, so "previous" is well defined.
    /// </summary>
    static LevelStep[] ResolveFadeStarts(List<(LevelStep Step, int Track, int Event, int Level, byte? From)> ordered)
    {
        var last = new Dictionary<(int Slot, int Index), LevelStep>();
        var result = new LevelStep[ordered.Count];
        for (int i = 0; i < ordered.Count; i++)
        {
            var (step, _, _, _, from) = ordered[i];
            var key = (step.Slot, step.Index);
            byte start = from ?? (last.TryGetValue(key, out var previous) ? previous.ValueAt(step.Start) : (byte)0);
            result[i] = step with { From = start };
            last[key] = result[i];
        }
        return result;
    }

    static List<(LevelStep Step, byte? From)> ResolveLevels(LevelsEvent e, TimelineContext context,
                                                             Dictionary<int, int> universeSlots, List<TimelineIssue> issues)
    {
        var result = new List<(LevelStep, byte?)>();
        if (e.Fade < TimeSpan.Zero)
        {
            issues.Add(Error(e, $"'{Describe(e)}' has a negative fade time."));
            return result;
        }

        PatchedFixture? fixture = null;
        int universe = e.Universe;
        if (e.FixtureId is { } fixtureId)
        {
            if (!context.Fixtures.TryGetValue(fixtureId, out fixture))
            {
                string why = context.AllFixtureIds.Contains(fixtureId)
                    ? "is disabled or has a patch error"
                    : "isn't in the show (it may have been removed)";
                issues.Add(Error(e, $"'{Describe(e)}' controls a fixture that {why}."));
                return result;
            }
            universe = fixture.Universe;
        }
        else if (!context.Universes.Contains(universe))
        {
            issues.Add(Error(e, $"'{Describe(e)}' uses universe {universe}, which the show doesn't define."));
            return result;
        }

        foreach (var level in e.Levels)
        {
            int address = fixture is null ? RawAddress(level) : FixtureAddress(fixture, level);
            if (!DmxUniverse.IsValidAddress(address))
            {
                issues.Add(Error(e, $"'{Describe(e)}': {DescribeLevel(level, fixture)} doesn't map to a DMX channel."));
                continue;
            }
            int slot = universeSlots.TryGetValue(universe, out var s) ? s : universeSlots[universe] = universeSlots.Count;
            result.Add((new LevelStep(e.Start.Ticks, e.Fade.Ticks, slot, address - 1, level.From ?? 0, level.To), level.From));
        }
        return result;
    }

    /// <summary>
    /// A lane's points as level steps, the same form lighting events compile to: the first point (and every step
    /// point) is a jump at its time; every other point is a fade from the previous point's level over the gap.
    /// </summary>
    /// <param name="offset">Where the lane's time 0 is on the timeline (a block's start).</param>
    /// <param name="end">Points after this (relative) time are left out (past a block's end).</param>
    /// <param name="issueId">What a problem is reported against: the track's channel, so its row is marked.</param>
    static List<LevelStep> CompileLane(ChannelLane lane, TimelineContext context, Dictionary<int, int> universeSlots, List<TimelineIssue> issues,
                                       TimeSpan offset = default, TimeSpan? end = null, Guid? issueId = null)
    {
        var result = new List<LevelStep>();
        if (lane.Points.Count == 0) return result;
        Guid reportAs = issueId ?? lane.Id;
        var target = lane.Target;
        int universe = target.Universe, address;
        if (target.FixtureId is { } fixtureId)
        {
            if (!context.Fixtures.TryGetValue(fixtureId, out var fixture))
            {
                string why = context.AllFixtureIds.Contains(fixtureId) ? "is disabled or has a patch error" : "isn't in the show (it may have been removed)";
                issues.Add(new TimelineIssue(TimelineIssueSeverity.Error, reportAs, $"A lighting channel controls a fixture that {why}."));
                return result;
            }
            universe = fixture.Universe;
            address = FixtureAddress(fixture, new Level(0, target.Channel, target.Control));
            if (!DmxUniverse.IsValidAddress(address))
            {
                issues.Add(new TimelineIssue(TimelineIssueSeverity.Error, reportAs, $"A lighting channel on '{fixture.Name}': {DescribeLevel(new Level(0, target.Channel, target.Control), fixture)} doesn't map to a DMX channel."));
                return result;
            }
        }
        else
        {
            address = target.Channel ?? 0;
            if (!context.Universes.Contains(universe) || !DmxUniverse.IsValidAddress(address))
            {
                issues.Add(new TimelineIssue(TimelineIssueSeverity.Error, reportAs, $"Lighting channel {universe}/{address} isn't in a universe the show defines."));
                return result;
            }
        }
        int slot = universeSlots.TryGetValue(universe, out var s) ? s : universeSlots[universe] = universeSlots.Count;

        var points = lane.Points.Where(p => end is null || p.Time <= end).OrderBy(p => p.Time)
            .Select(p => new CurvePoint { Time = p.Time + offset, Value = p.Value, Step = p.Step }).ToList();   // stable: equal times keep list order
        if (points.Count == 0) return result;
        if (points[0].Time < TimeSpan.Zero)
        {
            issues.Add(new TimelineIssue(TimelineIssueSeverity.Error, reportAs, "A lighting channel has a point before 0."));
            return result;
        }
        for (int i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (i == 0 || point.Step || point.Time == points[i - 1].Time)
                result.Add(new LevelStep(point.Time.Ticks, 0, slot, address - 1, point.Value, point.Value));
            else
            {
                var previous = points[i - 1];
                result.Add(new LevelStep(previous.Time.Ticks, (point.Time - previous.Time).Ticks, slot, address - 1, previous.Value, point.Value));
            }
        }
        return result;
    }

    static int RawAddress(Level level) => level.Channel ?? 0;

    static int FixtureAddress(PatchedFixture fixture, Level level)
    {
        if (level.Control is { } control) return fixture.TryGetAddress(control, out int address) ? address : 0;
        return level.Channel is { } channel ? fixture.AddressOfChannel(channel) : 0;
    }

    static AudioCue? CompileAudio(AudioEvent e, TimelineContext context, List<TimelineIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(e.File))
        {
            issues.Add(Error(e, $"'{Describe(e)}' has no audio file."));
            return null;
        }
        if (e.Offset < TimeSpan.Zero || e.FadeIn < TimeSpan.Zero || e.FadeOut < TimeSpan.Zero || e.Length < TimeSpan.Zero)
        {
            issues.Add(Error(e, $"'{Describe(e)}' has a negative offset, length or fade time."));
            return null;
        }

        string path = context.ResolveAudioPath(e.File);
        if (!File.Exists(path))
        {
            issues.Add(Error(e, $"'{Describe(e)}': audio file not found: {path}"));
            return null;
        }

        long? end = e.Length is { } length ? (e.Start + length).Ticks
                  : !e.Loop && context.AudioLength(path) is { } fileLength ? (e.Start + fileLength - e.Offset).Ticks
                  : null;
        return new AudioCue(e.Id, Describe(e), path, e.Start.Ticks, end, e.Offset.Ticks,
                            Math.Clamp(e.Volume, 0, 1), e.FadeIn.Ticks, e.FadeOut.Ticks, e.Loop);
    }

    static void CheckIds(ShowTimeline timeline, List<TimelineIssue> issues)
    {
        foreach (var duplicate in timeline.Tracks.SelectMany(t => t.Events).GroupBy(e => e.Id).Where(g => g.Count() > 1))
            issues.Add(new TimelineIssue(TimelineIssueSeverity.Warning, duplicate.Key, $"{duplicate.Count()} events share the ID {duplicate.Key}."));
    }

    public static string Describe(TimelineEvent e) => e.Name ?? e switch
    {
        AudioEvent a => Path.GetFileName(a.File),
        LevelsEvent => $"Levels at {e.Start:hh\\:mm\\:ss\\.fff}",
        _ => $"Event at {e.Start:hh\\:mm\\:ss\\.fff}",
    };

    static string DescribeLevel(Level level, PatchedFixture? fixture) =>
        level.Control is { } control ? $"its {control} control (the fixture's mode has none)"
        : fixture is null ? $"address {level.Channel}"
        : $"channel {level.Channel} (the fixture has {fixture.ChannelCount})";

    static TimelineIssue Error(TimelineEvent e, string message) => new(TimelineIssueSeverity.Error, e.Id, message);
}
