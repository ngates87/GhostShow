using System.Text;
using System.Text.RegularExpressions;

namespace GhostShow.Core.Import;

/// <summary>A fixture type in a VenueMagic project, e.g. "MF2:DMX-4".</summary>
/// <param name="Channels">Its DMX channels in order (VenueMagic's grouping channels, like "RGB", left out).</param>
public sealed record VmFixtureType(string Id, string Name, IReadOnlyList<string> Channels);

/// <summary>A patched fixture: its name, type and DMX start address (1-512, as VenueMagic shows it).</summary>
public sealed record VmFixture(string Id, string Name, string TypeId, int Address, IReadOnlyList<string> Channels);

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
        return new VenueMagicProject { FixtureTypes = types, Fixtures = fixtures, Timelines = ReadTimelines(text, Path.GetFileName(folder)), Folder = folder };
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
