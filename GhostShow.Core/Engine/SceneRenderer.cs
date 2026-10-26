using GhostShow.Core.Model;

namespace GhostShow.Core.Engine;

/// <summary>Pure functions: scene + time in, DMX frames and due audio clips out. Easy to test.</summary>
public static class SceneRenderer
{
    public static Dictionary<int, byte[]> CreateBuffers(IEnumerable<ChannelTrack> tracks)
    {
        var d = new Dictionary<int, byte[]>();
        foreach (var t in tracks)
            if (!d.ContainsKey(t.Universe)) d[t.Universe] = new byte[512];
        return d;
    }

    /// <summary>
    /// Renders all channel tracks at time tMs. Where several tracks drive the same channel,
    /// the highest value wins (HTP, the usual lighting-console rule).
    /// </summary>
    public static void Render(IReadOnlyList<ChannelTrack> tracks, int tMs, Dictionary<int, byte[]> buffers)
    {
        foreach (var b in buffers.Values) Array.Clear(b);
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            if (t.Channel < 1 || t.Channel > 512) continue;
            if (!buffers.TryGetValue(t.Universe, out var buf)) continue;
            var v = t.Evaluate(tMs);
            if (v is null) continue;
            if (v.Value > buf[t.Channel - 1]) buf[t.Channel - 1] = (byte)v.Value;
        }
    }

    /// <summary>Invokes onDue for clips with fromExclusive &lt; StartMs &lt;= toInclusive.</summary>
    public static void ForEachDueClip(IReadOnlyList<AudioTrack> tracks, int fromExclusive, int toInclusive,
                                      Action<AudioClip> onDue)
    {
        for (int i = 0; i < tracks.Count; i++)
        {
            var clips = tracks[i].Clips;
            for (int j = 0; j < clips.Count; j++)
            {
                var c = clips[j];
                if (c.StartMs > fromExclusive && c.StartMs <= toInclusive) onDue(c);
            }
        }
    }
}
