namespace GhostShow.Core.Inputs;

/// <summary>
/// Something an input did: a key, a serial command, and later a Stream Deck key or a GPIO pin, pressed or let go. Every
/// kind of input becomes these, so the buttons and the input monitor only deal with one thing (see docs/plan-triggers.md).
/// Source, Input and Address are the same as a button trigger's, which is how an event finds its button.
/// </summary>
/// <param name="Input">The show's input device it came from; null for the keyboard.</param>
public sealed record InputEvent(string Source, string Address, bool IsPressed, DateTimeOffset Time, Guid? Input = null);
