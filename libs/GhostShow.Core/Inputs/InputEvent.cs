namespace GhostShow.Core.Inputs;

/// <summary>
/// Something an input did: a key, a serial command, and later a Stream Deck key or a GPIO pin, pressed or let go. Every
/// kind of input becomes these, so the triggers and the input monitor only deal with one thing (see docs/plan-triggers.md).
/// Source, Input and Address are the same as a trigger's, which is how an event finds its triggers.
/// </summary>
/// <param name="IsPressed">A key: down or up. A serial command: always true (it arrived); its <paramref name="Value"/> says more.</param>
/// <param name="Input">The show's input device it came from; null for the keyboard.</param>
/// <param name="Value">A serial command's value: "On" for <c>BTN24=On</c> (whose address is "BTN24"); null for one without.</param>
public sealed record InputEvent(string Source, string Address, bool IsPressed, DateTimeOffset Time, Guid? Input = null, string? Value = null)
{
    /// <summary>
    /// A serial command as an input: <c>BTN24=On</c> is input "BTN24" with the value "On" (spaces around the '=' don't
    /// matter), as sensor boxes report a contact closing and opening; <c>BTN24</c> has no value. A command starting
    /// with '=' is all address.
    /// </summary>
    public static (string Address, string? Value) SplitCommand(string command)
    {
        int equals = command.IndexOf('=');
        if (equals <= 0) return (command, null);
        return (command[..equals].TrimEnd(), command[(equals + 1)..].Trim());
    }

    /// <summary>The command as sent: "BTN24=On", "BTN24".</summary>
    public string Command => Value is null ? Address : $"{Address}={Value}";
}
