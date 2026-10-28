using System.Text.Json.Nodes;

namespace GhostShow.Core.Storage;

/// <summary>
/// Timelines before timeline file version 5 could have marker tracks (named points in time that did nothing but
/// show their name when passed). Markers are gone: a scare is its own timeline, started when someone gets there.
/// This takes marker tracks, and any marker events on other tracks, out of a timeline's JSON before it's read.
/// </summary>
static class LegacyMarkers
{
    /// <summary>Removes them from a timeline object (one with a "tracks" array). Returns whether there were any.</summary>
    public static bool Drop(JsonNode? timeline)
    {
        if (Get(timeline as JsonObject, "tracks") is not JsonArray tracks) return false;
        bool dropped = false;
        foreach (var track in tracks.OfType<JsonObject>().ToList())
        {
            if (IsText(Get(track, "kind"), "Markers"))
            {
                tracks.Remove(track);
                dropped = true;
            }
            else if (Get(track, "events") is JsonArray events)
            {
                foreach (var marker in events.OfType<JsonObject>().Where(e => IsText(Get(e, "type"), "marker")).ToList())
                {
                    events.Remove(marker);
                    dropped = true;
                }
            }
        }
        return dropped;
    }

    static bool IsText(JsonNode? node, string text) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) && string.Equals(s, text, StringComparison.OrdinalIgnoreCase);

    static JsonNode? Get(JsonObject? obj, string name) =>
        obj?.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
