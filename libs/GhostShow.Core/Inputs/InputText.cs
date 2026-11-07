using System.Text;

namespace GhostShow.Core.Inputs;

/// <summary>Inputs in words, for the input monitor and trigger lists.</summary>
public static class InputText
{
    /// <summary>
    /// A command with its control characters shown, as VenueMagic's raw view does: "GO[1B]" for an Esc in it. Otherwise
    /// they'd be invisible, and two commands that look the same could differ.
    /// </summary>
    public static string Visible(string command)
    {
        if (!command.Any(char.IsControl)) return command;
        var text = new StringBuilder(command.Length + 8);
        foreach (char c in command) text.Append(char.IsControl(c) ? $"[{(int)c:X2}]" : c);
        return text.ToString();
    }
}
