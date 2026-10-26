using System.Text.Json.Serialization;

namespace GhostShow.Core.Model;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Interpolation { Linear, Step }

public sealed record Keyframe(int TimeMs, byte Value, Interpolation Interpolation = Interpolation.Linear);

public sealed record AudioClip(int StartMs, string File, int Volume = 100);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ChannelTrack), "channel")]
[JsonDerivedType(typeof(AudioTrack), "audio")]
public abstract class Track
{
    public string Name { get; set; } = "Track";
}

/// <summary>Keyframed value (0-255) for one DMX channel.</summary>
public sealed class ChannelTrack : Track
{
    public int Universe { get; set; }
    /// <summary>1-512</summary>
    public int Channel { get; set; } = 1;
    public List<Keyframe> Keyframes { get; set; } = new();

    // ---- editing API (the future UI calls these) ----

    /// <summary>Adds a keyframe, or replaces the one at the same time.</summary>
    public Keyframe SetKeyframe(int timeMs, byte value, Interpolation interp = Interpolation.Linear)
    {
        var kf = new Keyframe(Math.Max(0, timeMs), value, interp);
        int i = Keyframes.FindIndex(k => k.TimeMs == kf.TimeMs);
        if (i >= 0) { Keyframes[i] = kf; return kf; }
        int at = Keyframes.FindIndex(k => k.TimeMs > kf.TimeMs);
        if (at < 0) Keyframes.Add(kf); else Keyframes.Insert(at, kf);
        return kf;
    }

    public bool RemoveKeyframe(int timeMs) => Keyframes.RemoveAll(k => k.TimeMs == timeMs) > 0;

    public bool MoveKeyframe(int fromMs, int toMs)
    {
        var kf = Keyframes.FirstOrDefault(k => k.TimeMs == fromMs);
        if (kf is null) return false;
        Keyframes.Remove(kf);
        SetKeyframe(toMs, kf.Value, kf.Interpolation);
        return true;
    }

    /// <summary>Value at time t, or null if the track has no keyframes (it then outputs nothing).</summary>
    public int? Evaluate(int t)
    {
        var k = Keyframes;
        if (k.Count == 0) return null;
        if (t <= k[0].TimeMs) return k[0].Value;
        if (t >= k[^1].TimeMs) return k[^1].Value;

        // invariant: k[lo].TimeMs <= t < k[hi].TimeMs
        int lo = 0, hi = k.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (k[mid].TimeMs <= t) lo = mid; else hi = mid;
        }
        var a = k[lo]; var b = k[hi];
        if (a.Interpolation == Interpolation.Step) return a.Value;
        double f = (double)(t - a.TimeMs) / (b.TimeMs - a.TimeMs);
        return (int)Math.Round(a.Value + (b.Value - a.Value) * f);
    }
}

/// <summary>Sound clips placed on the timeline (fire-and-forget at their start time).</summary>
public sealed class AudioTrack : Track
{
    public List<AudioClip> Clips { get; set; } = new();

    public void AddClip(AudioClip clip)
    {
        int at = Clips.FindIndex(c => c.StartMs > clip.StartMs);
        if (at < 0) Clips.Add(clip); else Clips.Insert(at, clip);
    }

    public bool RemoveClip(AudioClip clip) => Clips.Remove(clip);

    public bool MoveClip(AudioClip clip, int newStartMs)
    {
        if (!Clips.Remove(clip)) return false;
        AddClip(clip with { StartMs = Math.Max(0, newStartMs) });
        return true;
    }
}
