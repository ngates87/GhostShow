using System.Text.RegularExpressions;

namespace GhostShow.Core.Import;

/// <summary>A Run Timeline's settings: the timeline (by name), its name in the cue list, and Play looped / Play one only.</summary>
public sealed record VmRun(string Timeline, string CueName, bool Loop, bool OneOnly);

/// <summary>
/// A Cue List Command (an event, or a cue button's): what it does to which cue-list item (by its name in the cue list).
/// Its numbers, as VenueMagic's setup page lists them: 0 Stop this, 1 Pause this, 2 Stop item, 3 Pause item, 4 Resume
/// item, 5 End loop, 6 Stop all, 7 Pause all, 8 Resume all, 9 End all loops, 10 Mute all, 11 Unmute all, 12 Disable
/// control, 13 Enable control.
/// </summary>
public sealed record VmCueListCommand(int Command, string Item)
{
    public const int StopItem = 2, EndLoop = 5, StopAll = 6;

    static readonly string[] Names =
    [
        "Stop this", "Pause this", "Stop item", "Pause item", "Resume item", "End loop", "Stop all", "Pause all",
        "Resume all", "End all loops", "Mute all", "Unmute all", "Disable control", "Enable control",
    ];

    public string Name => Command >= 0 && Command < Names.Length ? Names[Command] : $"command {Command}";
}

/// <summary>
/// Event records VenueMagic writes in timelines (event tracks) and in cue buttons, read by their shapes (see
/// docs/import-venuemagic.md). Text is the file as Latin-1, so string positions are byte positions.
/// </summary>
static class VenueMagicRecords
{
    // Version 475, [len]timeline, [len]name in the cue list, then the master volume and lamp level (percent, doubles),
    // then Play looped and Play one only (a byte each).
    static readonly Regex Run = new(@"\xdb\x01\x00\x00([\x01-\x7f])", RegexOptions.Compiled);

    // After its "Cue List Command" labels: FF×4, version 475, the command (an int), [len]the item's name in the cue list.
    static readonly Regex Command = new(@"\xff{4}\xdb\x01\x00\x00([\x00-\x3f])\x00\x00\x00([\x00-\x7f])", RegexOptions.Compiled);

    /// <summary>The first Run Timeline settings in text[from..to], or null.</summary>
    public static VmRun? FindRun(string text, int from, int to)
    {
        to = Math.Min(to, text.Length);
        for (var m = Run.Match(text, from); m.Success && m.Index < to; m = m.NextMatch())
        {
            int at = m.Groups[1].Index, length = text[at];
            int cueAt = at + 1 + length;
            if (cueAt + 1 > text.Length || !Printable(text, at + 1, length)) continue;
            int cueLength = text[cueAt], rest = cueAt + 1 + cueLength;
            if (rest + 18 > text.Length || !Printable(text, cueAt + 1, cueLength)) continue;
            double volume = Double(text, rest), lamp = Double(text, rest + 8);
            if (volume is < 0 or > 100 || lamp is < 0 or > 100) continue;
            int loop = text[rest + 16], oneOnly = text[rest + 17];
            if (loop > 1 || oneOnly > 1) continue;
            return new VmRun(text.Substring(at + 1, length), text.Substring(cueAt + 1, cueLength), loop == 1, oneOnly == 1);
        }
        return null;
    }

    /// <summary>The first Cue List Command's settings in text[from..to], or null.</summary>
    public static VmCueListCommand? FindCommand(string text, int from, int to)
    {
        to = Math.Min(to, text.Length);
        int label = text.IndexOf("\u0010Cue List Command", from, Math.Max(0, to - from), StringComparison.Ordinal);
        if (label < 0) return null;
        for (var m = Command.Match(text, label); m.Success && m.Index < to; m = m.NextMatch())
        {
            int at = m.Groups[2].Index, length = text[at];
            if (Printable(text, at + 1, length)) return new VmCueListCommand(text[m.Groups[1].Index], text.Substring(at + 1, length));
        }
        return null;
    }

    static bool Printable(string text, int at, int length)
    {
        if (at + length > text.Length) return false;
        for (int i = at; i < at + length; i++)
            if (text[i] is < ' ' or > '~') return false;
        return true;
    }

    public static double Double(string text, int at)
    {
        if (at + 8 > text.Length) return double.NaN;
        Span<byte> bytes = stackalloc byte[8];
        for (int i = 0; i < 8; i++) bytes[i] = (byte)text[at + i];
        return BitConverter.ToDouble(bytes);
    }
}
