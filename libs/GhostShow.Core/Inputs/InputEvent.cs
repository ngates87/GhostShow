namespace GhostShow.Core.Inputs;

/// <summary>
/// Something an input did: a key, and later a sensor or a Stream Deck key, pressed or let go. Every kind of input becomes
/// these, so the buttons and the input monitor only deal with one thing (see docs/plan-triggers.md). Source and Address
/// are the same as a button trigger's, which is how an event finds its button.
/// </summary>
public sealed record InputEvent(string Source, string Address, bool IsPressed, DateTimeOffset Time);
