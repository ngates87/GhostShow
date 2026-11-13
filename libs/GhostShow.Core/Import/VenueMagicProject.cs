using System.Text;
using System.Text.RegularExpressions;

namespace GhostShow.Core.Import;

/// <summary>A fixture type in a VenueMagic project, e.g. "MF2:DMX-4".</summary>
/// <param name="Channels">Its DMX channels in order (VenueMagic's grouping channels, like "RGB", left out).</param>
public sealed record VmFixtureType(string Id, string Name, IReadOnlyList<string> Channels);

/// <summary>A patched fixture: its name, type and DMX start address (1-512, as VenueMagic shows it).</summary>
public sealed record VmFixture(string Id, string Name, string TypeId, int Address, IReadOnlyList<string> Channels);

/// <summary>
/// A cue button (VenueMagic's show buttons, in groups). Kind is what it plays: 0 a timeline (Target, by name), 1 a
/// playlist, 2 an audio file, 3 an event (<see cref="Run"/> or <see cref="Command"/>, when it's one of those).
/// </summary>
/// <param name="Caption">As shown, without the "&amp;" that marks its key.</param>
/// <param name="Key">The key that presses it (the character after "&amp;" in its caption), or null.</param>
/// <param name="Color">Its face colour, "#RRGGBB".</param>
/// <param name="CueName">Its name in the cue list (what a Cue List Command names it by); empty: the timeline's.</param>
public sealed record VmCueButton(string Caption, char? Key, string Group, int Kind, string Target, string Color, bool Loop, bool OneOnly,
                                 string CueName, VmRun? Run, VmCueListCommand? Command)
{
    public const int Timeline = 0, Playlist = 1, AudioFile = 2, Event = 3;
}

/// <summary>
/// What GhostShow reads from a VenueMagic 3/4 project file (.vmp, or its .vmp.bak backup): the patch, and the
/// timeline files it lists. VenueMagic saves with Windows MFC serialization, which isn't self-describing and isn't
/// documented, so this reads the records it needs by their recognisable shapes (length-prefixed names next to
/// "$guid" IDs) rather than decoding the whole file. Worked out from a VenueMagic 4 project (record version 475);
/// see docs/import-venuemagic.md.
/// </summary>
public sealed class VenueMagicProject
{
    const string Guid = @"\$[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";

    // Latin-1 keeps one char per byte, so regex positions are byte positions.
    static readonly Encoding Bytes = Encoding.Latin1;

    public required IReadOnlyList<VmFixtureType> FixtureTypes { get; init; }
    public required IReadOnlyList<VmFixture> Fixtures { get; init; }

    /// <summary>The project's timeline files, as paths relative to the project's folder ("Zone 2/cages/cage-lights.vmt").</summary>
    public required IReadOnlyList<string> Timelines { get; init; }

    /// <summary>The cue buttons, group by group (VenueMagic 4 projects, record version 475; empty for older ones).</summary>
    public IReadOnlyList<VmCueButton> Buttons { get; init; } = [];

    /// <summary>The folder the project file is in (its timeline and audio files are under it).</summary>
    public string Folder { get; init; } = "";

    public static VenueMagicProject Read(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 16 || Bytes.GetString(data, 0, 16) is not { } head || !head.Contains("CVMProject"))
            throw new InvalidDataException("This isn't a VenueMagic project file (no CVMProject record at its start).");
        var text = Bytes.GetString(data);
        var fixtures = ReadFixtures(data, text);
        var types = ReadTypes(text, fixtures);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        return new VenueMagicProject
        {
            FixtureTypes = types, Fixtures = fixtures, Timelines = ReadTimelines(text, Path.GetFileName(folder)), Folder = folder,
            Buttons = ReadButtons(text),
        };
    }

    static readonly Regex ButtonHead = new(@"\xdb\x01\x00\x00([\x01-\x7f])", RegexOptions.Compiled);
    static readonly Regex GroupName = new(@"([\x01-\x7f])([ -~]{1,127}?)[\s\S]{16}\x16Cue Button Group Notes", RegexOptions.Compiled);

    /// <summary>
    /// Cue buttons (after "CCueButtonList"; VenueMagic 4's layout, record version 475): each group's buttons, then the
    /// group (its name, 16 bytes, "Cue Button Group Notes"). A button: version 475, [len]caption, [len]what it plays,
    /// its caption and face colours (COLORREF), master volume (a double, percent), then bytes: cueing mode, kind, Play
    /// looped, Play one only, …; its master lamp level (a double) 41 bytes after the colours; its font; its name in the
    /// cue list, just before "Cue Button Notes"; and, for an event button, the event's record.
    /// </summary>
    static List<VmCueButton> ReadButtons(string text)
    {
        int start = text.IndexOf("CCueButtonList\u00db\u0001\u0000\u0000", StringComparison.Ordinal);
        if (start < 0) return [];
        int end = text.IndexOf("CVMSupportedDevice", start, StringComparison.Ordinal);
        if (end < 0) end = text.Length;

        var heads = new List<(int At, string Caption, string Target, int Fields)>();
        for (var m = ButtonHead.Match(text, start); m.Success && m.Index < end; m = m.NextMatch())
        {
            int at = m.Groups[1].Index, length = text[at], targetAt = at + 1 + length;
            if (targetAt >= end || !Printable(text, at + 1, length)) continue;
            int targetLength = text[targetAt], fields = targetAt + 1 + targetLength;
            if (targetLength > 127 || !Printable(text, targetAt + 1, targetLength) || fields + 49 > end) continue;
            if (text[fields + 3] != 0 || text[fields + 7] != 0) continue;   // two colours (R, G, B, 0)
            double volume = VenueMagicRecords.Double(text, fields + 8), lamp = VenueMagicRecords.Double(text, fields + 41);
            if (volume is < 0 or > 100 || lamp is < 0 or > 100) continue;
            if (text.IndexOf("\u0010Cue Button Notes", fields, Math.Min(400, end - fields), StringComparison.Ordinal) < 0) continue;
            heads.Add((m.Index, text.Substring(at + 1, length), text.Substring(targetAt + 1, targetLength), fields));
        }
        var groups = GroupName.Matches(text[start..end]).Where(g => g.Groups[2].Length == g.Groups[1].Value[0])
            .Select(g => (At: start + g.Index, Name: g.Groups[2].Value)).ToList();

        var buttons = new List<VmCueButton>();
        for (int i = 0; i < heads.Count; i++)
        {
            var (at, caption, target, fields) = heads[i];
            int next = i + 1 < heads.Count ? heads[i + 1].At : end;
            string group = groups.FirstOrDefault(g => g.At > at).Name ?? "";
            int kind = text[fields + 17];
            bool loop = text[fields + 18] == 1, oneOnly = text[fields + 19] == 1;
            string color = $"#{(int)text[fields + 4]:X2}{(int)text[fields + 5]:X2}{(int)text[fields + 6]:X2}";
            int notes = text.IndexOf("\u0010Cue Button Notes", fields, StringComparison.Ordinal);
            string cueName = CueNameBefore(text, notes);
            VmRun? run = null;
            VmCueListCommand? command = null;
            if (kind == VmCueButton.Event)
            {
                run = text.IndexOf("Run Timeline", notes, next - notes, StringComparison.Ordinal) >= 0 ? VenueMagicRecords.FindRun(text, notes, next) : null;
                command = run is null ? VenueMagicRecords.FindCommand(text, notes, next) : null;
            }
            var (shown, key) = Caption(caption);
            buttons.Add(new VmCueButton(shown, key, group, kind, target, color, loop, oneOnly, cueName, run, command));
        }
        return buttons;
    }

    /// <summary>The name in the cue list: the length-prefixed text that ends one byte before "Cue Button Notes".</summary>
    static string CueNameBefore(string text, int notes)
    {
        for (int length = 0; length < 128 && notes - 2 - length >= 0; length++)
        {
            int at = notes - 2 - length;
            if (text[at] == length && Printable(text, at + 1, length)) return text.Substring(at + 1, length);
            if (length > 0 && text[at + 1] is < ' ' or > '~') break;
        }
        return "";
    }

    /// <summary>"&amp;Scare" → ("Scare", 'S'); "&amp;&amp;" is a plain "&amp;".</summary>
    static (string Caption, char? Key) Caption(string caption)
    {
        var shown = new StringBuilder();
        char? key = null;
        for (int i = 0; i < caption.Length; i++)
        {
            if (caption[i] == '&' && i + 1 < caption.Length)
            {
                i++;
                if (caption[i] != '&') key ??= char.ToUpperInvariant(caption[i]);
            }
            shown.Append(caption[i]);
        }
        return (shown.ToString(), key);
    }

    static bool Printable(string text, int at, int length)
    {
        if (at + length > text.Length) return false;
        for (int i = at; i < at + length; i++)
            if (text[i] is < ' ' or > '~') return false;
        return true;
    }

    /// <summary>
    /// Patched fixtures: [address-ish int][address int][len]name[36]$typeId 0 [36]$id, then its channels, each with a
    /// "Channel Group Notes" string before its name. The int just before the name is the DMX start address.
    /// </summary>
    static List<VmFixture> ReadFixtures(byte[] data, string text)
    {
        var headers = new List<(int Start, int End, string Name, string Type, string Id)>();
        foreach (Match m in Regex.Matches(text, @"([\x01-\x7f])([ -~]{1,127}?)(" + Guid + @")\x00(" + Guid + ")"))
        {
            if (m.Groups[2].Length != m.Groups[1].Value[0]) continue;
            headers.Add((m.Index, m.Index + m.Length, m.Groups[2].Value, m.Groups[3].Value[1..], m.Groups[4].Value[1..]));
        }
        var fixtures = new List<VmFixture>();
        for (int k = 0; k < headers.Count; k++)
        {
            var (start, end, name, type, id) = headers[k];
            if (start < 8) continue;
            int address = BitConverter.ToInt32(data, start - 4);
            int stop = k + 1 < headers.Count ? headers[k + 1].Start : text.Length;
            var channels = new List<string>();
            foreach (Match c in Regex.Matches(text[end..stop], @"\x13Channel Group Notes" + Guid + @"([\x00-\x7f])"))
            {
                int at = end + c.Index, nameAt = end + c.Index + c.Length, length = c.Groups[1].Value[0];
                int number = BitConverter.ToInt32(data, at - 15);   // VenueMagic's own channel number
                if (number is < 0 or > 100_000) continue;           // a grouping channel ("RGB"): no DMX slot
                channels.Add(text.Substring(nameAt, length));
            }
            if (address is >= 1 and <= 512 && channels.Count > 0) fixtures.Add(new VmFixture(id, name, type, address, channels));
        }
        return fixtures;
    }

    /// <summary>Fixture types: [len]name [int] [36]$typeId. Their channels are taken from a fixture of the type.</summary>
    static List<VmFixtureType> ReadTypes(string text, IReadOnlyList<VmFixture> fixtures)
    {
        var names = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(text, @"([\x01-\x7f])([ -~]{1,120}?)[\x00-\xff]{4}(" + Guid + ")"))
        {
            if (m.Groups[2].Length != m.Groups[1].Value[0]) continue;
            names.TryAdd(m.Groups[3].Value[1..], m.Groups[2].Value);
        }
        return fixtures.GroupBy(f => f.TypeId)
            .Select(g => new VmFixtureType(g.Key, names.GetValueOrDefault(g.Key) ?? $"Fixture type {g.Key[..8]}", g.First().Channels))
            .ToList();
    }

    /// <summary>Timeline files the project lists: [len]C:\...\&lt;project folder&gt;\...\name.vmt, made relative to the project's folder.</summary>
    static List<string> ReadTimelines(string text, string projectFolder)
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(text, @"[A-Za-z]:\\[ -~]{1,240}?\.vmt"))
        {
            var parts = m.Value.Split('\\');
            int folder = Array.FindLastIndex(parts, p => string.Equals(p, projectFolder, StringComparison.OrdinalIgnoreCase));
            if (folder < 0 || folder == parts.Length - 1) continue;   // a timeline outside the project
            found.Add(string.Join('/', parts[(folder + 1)..]));
        }
        return found.ToList();
    }
}
