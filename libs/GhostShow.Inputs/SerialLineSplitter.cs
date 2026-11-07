using System.Text;

namespace GhostShow.Inputs;

/// <summary>
/// Turns the bytes a serial device sends into commands, one per line, as VenueMagic does: a command ends at a carriage
/// return (13) or a line feed (10), so CR, LF and CR+LF all work. Blank lines are skipped and spaces around a command
/// are trimmed. A device that never ends its lines still gets heard: whatever has arrived counts as a command once the
/// line goes quiet (<see cref="Flush"/>) or gets too long for a command (wrong baud rate, say).
/// </summary>
public sealed class SerialLineSplitter
{
    /// <summary>Longer than any real command; past this, it's noise and is taken as a line so it shows in the monitor.</summary>
    public const int MaxLength = 200;

    readonly StringBuilder _line = new();

    /// <summary>Adds bytes as they arrive; returns the commands they complete (often none).</summary>
    public IReadOnlyList<string> Add(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<string>();
        foreach (byte b in bytes)
        {
            if (b is 13 or 10) Take(lines);
            else
            {
                // Latin-1: each byte is one character, so odd bytes still show (as [xx] in the monitor) instead of vanishing.
                _line.Append((char)b);
                if (_line.Length >= MaxLength) Take(lines);
            }
        }
        return lines;
    }

    /// <summary>The line went quiet: what has arrived of it, if anything, is a command.</summary>
    public string? Flush()
    {
        var lines = new List<string>(1);
        Take(lines);
        return lines.Count > 0 ? lines[0] : null;
    }

    void Take(List<string> lines)
    {
        string line = _line.ToString().Trim();
        _line.Clear();
        if (line.Length > 0) lines.Add(line);
    }
}
