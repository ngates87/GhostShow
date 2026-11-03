using System.Text;
using System.Text.RegularExpressions;

namespace GhostShow.Core.Import;

/// <summary>A handle of a VenueMagic envelope: where it is (percent of the clip, 0-100) and its level (percent).</summary>
public sealed record VmHandle(int Index, double Position, double Level);

/// <summary>One channel's envelope in a Level Control clip ("Zone_3_Area_8_Dim_1.Channel_3").</summary>
public sealed record VmEnvelope(string Channel, IReadOnlyList<VmHandle> Handles);

public abstract record VmItem(double Start, double Length);

/// <summary>A Level Control clip: its envelopes (each from where the previous clip left the channel, as in GhostShow).</summary>
public sealed record VmLevelClip(double Start, double Length, IReadOnlyList<VmEnvelope> Envelopes) : VmItem(Start, Length);

/// <summary>An audio clip: the file (as VenueMagic stored it), and its volume envelope's start and end, if read.</summary>
public sealed record VmAudioClip(double Start, double Length, string Name, string File, double? VolumeStart, double? VolumeEnd) : VmItem(Start, Length);

/// <summary>An expression effect (VenueMagic's built-in lamp effects, e.g. "Lightning Storm", "Brownout").</summary>
public sealed record VmExpression(double Start, double Length, string Name) : VmItem(Start, Length);

/// <summary>An event track's Run Timeline: plays another timeline (by name) at a time.</summary>
public sealed record VmRunTimeline(double Start, string Timeline) : VmItem(Start, 0);

/// <param name="Kind">"Lamp" or "Control" (lighting), "Audio" or "Event".</param>
/// <param name="Channels">Lighting: the channels it controls, as "Fixture.Channel" (in first-seen order).</param>
public sealed record VmTrack(string Kind, string Name, IReadOnlyList<string> Channels, IReadOnlyList<VmItem> Items);

/// <summary>
/// A VenueMagic timeline file (.vmt): its tracks, with the clips, effects and events on them. Like
/// <see cref="VenueMagicProject"/>, it reads records by their recognisable shapes (see docs/import-venuemagic.md):
/// - every track and timeline item starts with a common header (FF×8, 1, ten bytes, 1) followed by its start, a
///   double and its length, in seconds; then, for a track, "… Track Notes" and its name; for an audio clip its name,
///   "Audio Clip Notes" and its file; for an expression effect its name and "Lamp Effect Notes"; for an event "Run
///   Timeline", "Event Notes" and the timeline it runs;
/// - a Level Control clip ends with its start and length before its name, "Level Control", preceded by its
///   envelopes, each: the channel's ID ("GIID_n.Level") and its handles (index, flag, position in percent of the clip;
///   the level, in percent, 36 bytes earlier). Elsewhere in the file each ID is followed by its channel's name
///   ("GIID_n.Level", "Fixture.Channel"), which says which channel an envelope is.
/// Files written by older VenueMagic versions (record version 381) are laid out differently and aren't read.
/// </summary>
public sealed class VenueMagicTimeline
{
    static readonly Encoding Bytes = Encoding.Latin1;
    static readonly Regex Header = new(@"\xff{8}\x01\x00\x00\x00[\s\S]{10}\x01\x00\x00\x00([\s\S]{8})[\s\S]{8}([\s\S]{8})", RegexOptions.Compiled);
    static readonly Regex LevelControl = new(@"\x0dLevel Control\x00", RegexOptions.Compiled);
    static readonly Regex ChannelRef = new(@"(GIID_\d+)\.Level([\x01-\x7f])", RegexOptions.Compiled);
    static readonly Regex Handle = new(@"([\x01-\x40])\x01([\s\S]{8})\x00{12}[\xdb\x7d]\x01\x00\x00\x00{4}[\s\S]{16}\x00", RegexOptions.Compiled);
    static readonly Regex Printable = new(@"[ -~]{3,}", RegexOptions.Compiled);

    public required IReadOnlyList<VmTrack> Tracks { get; init; }

    /// <summary>The record version the file was written with (475: VenueMagic 4; 381: older).</summary>
    public required int Version { get; init; }

    public static VenueMagicTimeline Read(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 8) throw new InvalidDataException("This isn't a VenueMagic timeline file.");
        int version = BitConverter.ToInt32(data, 0);
        if (version != 475) return new VenueMagicTimeline { Tracks = [], Version = version };
        var text = Bytes.GetString(data);

        // Everything recognisable, in file order.
        var marks = new List<(int At, object What)>();
        foreach (Match m in Header.Matches(text))
            if (Item(text, m, D(data, m.Groups[1].Index), D(data, m.Groups[2].Index)) is { } item) marks.Add((m.Index, item));
        foreach (Match m in LevelControl.Matches(text))
            if (m.Index >= 44) marks.Add((m.Index, new ClipMark(D(data, m.Index - 44), D(data, m.Index - 28))));
        // "GIID_n.Level" (VenueMagic's ID for a channel), then a channel name ("Fixture.Channel"): which channel the ID is.
        // Followed by anything else, it's the reference that starts an envelope (in a Level Control clip) for that ID.
        var names = new Dictionary<string, string>();
        foreach (Match m in ChannelRef.Matches(text))
        {
            int length = m.Groups[2].Value[0], at = m.Index + m.Length;
            var channel = at + length <= text.Length ? text.Substring(at, length) : "";
            if (channel.Contains('.') && channel.All(c => c is >= ' ' and <= '~'))
            {
                names.TryAdd(m.Groups[1].Value, channel);
                marks.Add((m.Index, new ChannelMark(channel)));
            }
            else marks.Add((m.Index, new EnvelopeMark(m.Groups[1].Value)));
        }
        foreach (Match m in Handle.Matches(text))
        {
            double position = D(data, m.Groups[2].Index);
            if (position is < -0.001 or > 100.001 || m.Index < 36) continue;
            double level = D(data, m.Index - 36);
            if (level is < -0.001 or > 100.001) continue;
            marks.Add((m.Index, new VmHandle(m.Groups[1].Value[0], position, level)));
        }
        marks.Sort((a, b) => a.At.CompareTo(b.At));

        // Walk them: a track header starts a track; handles belong to the channel named before them (or, without
        // one, to an audio clip's volume and pan); a Level Control record closes a clip with the envelopes since.
        var tracks = new List<VmTrack>();
        string? kind = null, name = null;
        var channels = new List<string>();
        var items = new List<VmItem>();
        var envelopes = new List<(string? Channel, List<VmHandle> Handles)>();
        var loose = new List<VmHandle>();
        void EndTrack()
        {
            if (kind is not null) tracks.Add(new VmTrack(kind, name ?? "", channels.ToList(), items.ToList()));
            channels.Clear();
            items.Clear();
            envelopes.Clear();
            loose.Clear();
        }
        foreach (var (_, what) in marks)
        {
            switch (what)
            {
                case TrackMark track:
                    EndTrack();
                    (kind, name) = (track.Kind, track.Name);
                    break;
                case EnvelopeMark envelope:
                    envelopes.Add((names.GetValueOrDefault(envelope.Id), new List<VmHandle>()));
                    break;
                case ChannelMark channel:
                    if (!channels.Contains(channel.Name)) channels.Add(channel.Name);
                    break;
                case VmHandle handle:
                    if (envelopes.Count > 0) envelopes[^1].Handles.Add(handle);
                    else loose.Add(handle);
                    break;
                case ClipMark clip:
                    var merged = envelopes.Where(e => e.Channel is not null && e.Handles.Count > 0).GroupBy(e => e.Channel!)
                        .Select(g => new VmEnvelope(g.Key, g.SelectMany(e => e.Handles).ToList())).ToList();
                    items.Add(new VmLevelClip(clip.Start, clip.Length, merged));
                    envelopes.Clear();
                    loose.Clear();
                    break;
                case VmAudioClip audio:
                    // The handles just before it: its volume envelope (start, end), then its pan.
                    var volume = loose.Take(2).ToList();
                    items.Add(audio with
                    {
                        VolumeStart = volume.FirstOrDefault(h => h.Position < 50)?.Level,
                        VolumeEnd = volume.FirstOrDefault(h => h.Position >= 50)?.Level,
                    });
                    loose.Clear();
                    envelopes.Clear();
                    break;
                case VmItem item:
                    items.Add(item);
                    loose.Clear();
                    break;
            }
        }
        EndTrack();
        return new VenueMagicTimeline { Tracks = tracks, Version = version };
    }

    sealed record TrackMark(string Kind, string Name);
    sealed record ChannelMark(string Name);
    sealed record EnvelopeMark(string Id);
    sealed record ClipMark(double Start, double Length);

    /// <summary>What follows a common header: a track, an audio clip, an expression effect or a Run Timeline event.</summary>
    static object? Item(string text, Match header, double start, double length)
    {
        int from = header.Index + header.Length;
        var runs = Printable.Matches(text.Substring(from, Math.Min(400, text.Length - from))).Select(r => r.Value).ToList();
        if (runs.Count < 2) return null;
        // Each kind has its notes label at a fixed place: a track's first ("Lamp Track Notes", then its name); a clip's
        // or effect's second (after its name). Further on is the next record's text, so nothing else is looked at.
        if (runs[0].EndsWith("Track Notes", StringComparison.Ordinal)) return new TrackMark(runs[0].Split(' ')[0], runs[1]);
        if (runs[0] == "Run Timeline" && runs[1] == "Event Notes" && runs.Count > 2)
            return new VmRunTimeline(start, runs.Count > 3 ? runs[3] : runs[2]);
        if (runs[1] == "Audio Clip Notes")
        {
            var file = runs.Skip(2).Take(3).Select(r => Regex.Match(r, @"[A-Za-z]:\\.*\.(mp3|wav|flac|ogg|aiff?)$", RegexOptions.IgnoreCase)).FirstOrDefault(m => m.Success)?.Value;
            return file is null ? null : new VmAudioClip(start, length, runs[0], file, null, null);
        }
        if (runs[1] == "Lamp Effect Notes") return new VmExpression(start, length, runs[0]);
        return null;
    }

    static double D(byte[] data, int at) => at + 8 <= data.Length ? BitConverter.ToDouble(data, at) : double.NaN;
}
