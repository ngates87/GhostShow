using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GhostShow.Core.Model;
using GhostShow.Core.Timeline;

namespace GhostShow.Core.Storage;

/// <summary>
/// Reads the prototype's "scenes" (show files before version 4) and converts them to timelines, so old shows keep
/// working. Keyframed channel tracks become levels events: a linear segment between two keyframes is a fade, a step
/// is a snap. Audio clips become audio events. Only used while loading; nothing writes this shape any more.
/// </summary>
static class LegacyScenes
{
    sealed record Keyframe(int TimeMs, byte Value, string Interpolation = "Linear");
    sealed record Clip(int StartMs, string File, int Volume = 100);

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
    [JsonDerivedType(typeof(ChannelTrack), "channel")]
    [JsonDerivedType(typeof(AudioTrack), "audio")]
    abstract class Track { public string Name { get; set; } = "Track"; }

    sealed class ChannelTrack : Track
    {
        public int Universe { get; set; }
        public int Channel { get; set; } = 1;
        public List<Keyframe> Keyframes { get; set; } = [];
    }

    sealed class AudioTrack : Track { public List<Clip> Clips { get; set; } = []; }

    sealed class Scene
    {
        public string Name { get; set; } = "Scene";
        public int DurationMs { get; set; } = 10_000;
        public bool Loop { get; set; }
        public List<Track> Tracks { get; set; } = [];
    }

    /// <param name="zeroBasedUniverses">True for files before version 3, whose universes counted from 0.</param>
    /// <returns>The converted timelines; empty if the file has no scenes.</returns>
    public static List<ShowTimeline> Convert(JsonNode? scenesNode, bool zeroBasedUniverses)
    {
        if (scenesNode is not JsonArray) return [];
        var scenes = scenesNode.Deserialize<List<Scene>>(Json.Options) ?? [];
        return scenes.Select(scene => new ShowTimeline
        {
            Name = scene.Name,
            Length = TimeSpan.FromMilliseconds(scene.DurationMs),
            Loop = scene.Loop,
            Tracks = scene.Tracks.Select(track => track switch
            {
                ChannelTrack channel => ConvertChannel(channel, zeroBasedUniverses),
                AudioTrack audio => ConvertAudio(audio),
                _ => new TimelineTrack { Name = track.Name },
            }).ToList(),
        }).ToList();
    }

    static TimelineTrack ConvertChannel(ChannelTrack track, bool zeroBasedUniverses)
    {
        int universe = track.Universe + (zeroBasedUniverses ? 1 : 0);
        var keys = track.Keyframes.OrderBy(k => k.TimeMs).ToList();
        var events = new List<TimelineEvent>();
        LevelsEvent Snap(Keyframe k) => Levels(k.TimeMs, 0, null, k.Value);
        LevelsEvent Levels(int startMs, int fadeMs, byte? from, byte to) => new()
        {
            Universe = universe,
            Start = TimeSpan.FromMilliseconds(startMs),
            Fade = TimeSpan.FromMilliseconds(fadeMs),
            Levels = [new Level(to, Channel: track.Channel, From: from)],
        };

        if (keys.Count > 0) events.Add(Snap(keys[0]));
        for (int i = 1; i < keys.Count; i++)
        {
            var (previous, next) = (keys[i - 1], keys[i]);
            events.Add(previous.Interpolation == "Step"
                ? Snap(next)
                : Levels(previous.TimeMs, next.TimeMs - previous.TimeMs, previous.Value, next.Value));
        }
        return new TimelineTrack { Name = track.Name, Kind = TrackKind.Lighting, Events = events };
    }

    static TimelineTrack ConvertAudio(AudioTrack track) => new()
    {
        Name = track.Name,
        Kind = TrackKind.Audio,
        Events = track.Clips.Select(c => (TimelineEvent)new AudioEvent
        {
            Start = TimeSpan.FromMilliseconds(c.StartMs),
            File = c.File,
            Volume = Math.Clamp(c.Volume, 0, 100) / 100.0,
        }).ToList(),
    };
}
