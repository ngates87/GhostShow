using System.Text.Json.Nodes;
using GhostShow.Core.Model;
using GhostShow.Core.Timeline;

namespace GhostShow.Core.Storage;

/// <summary>
/// Before show version 7, a timeline's own Loop and "Play one at a time" decided how every run of it played. From 7,
/// what runs a timeline (a button's Run Timeline, an event track's Run event) says whether that run loops and whether
/// it plays only if the timeline isn't playing already, as VenueMagic's do. Opening an older show gives each Run the
/// settings of the timeline it runs, so the show plays as it did. The timeline keeps its Loop, which now only applies
/// when it's played on its own (the editor's transport).
/// </summary>
static class LegacyRunSettings
{
    /// <summary>Whether a timeline file's playback settings had "Play one at a time" on.</summary>
    public static bool OneAtATime(JsonNode? timelineFile)
    {
        var playback = Property(timelineFile as JsonObject, "playback") as JsonObject;
        return Property(playback, "oneAtATime") is JsonValue value && value.TryGetValue<bool>(out bool on) && on;
    }

    /// <param name="oneAtATime">The timelines that had "Play one at a time" on.</param>
    public static void Apply(Project project, IReadOnlySet<Guid> oneAtATime)
    {
        var timelines = new Dictionary<Guid, ShowTimeline>();
        foreach (var timeline in project.Timelines) timelines.TryAdd(timeline.Id, timeline);

        foreach (var timeline in project.Timelines)
            foreach (var e in timeline.Tracks.SelectMany(t => t.Events).OfType<TimelineCommandEvent>())
            {
                if (e.Command != TimelineCommand.Run || !timelines.TryGetValue(e.TimelineId, out var target)) continue;
                var (loop, oneOnly) = (target.Loop, oneAtATime.Contains(target.Id));
                if (e.Loop == loop && e.OneOnly == oneOnly) continue;
                (e.Loop, e.OneOnly) = (loop, oneOnly);
                timeline.HasUnsavedChanges = true;   // written in the new form on the next save
            }

        foreach (var run in project.Buttons.SelectMany(b => b.Actions).OfType<RunTimelineAction>())
            if (timelines.TryGetValue(run.TimelineId, out var target))
                (run.Loop, run.OneOnly) = (target.Loop, oneAtATime.Contains(target.Id));
    }

    static JsonNode? Property(JsonObject? obj, string name) =>
        obj?.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
