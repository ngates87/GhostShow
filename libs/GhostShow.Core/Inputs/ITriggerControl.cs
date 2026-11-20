namespace GhostShow.Core.Inputs;

/// <summary>
/// The show's triggers, as a device sees and works them. They run on the show computer only (docs/plan-shared-view.md,
/// decision 1): a device shows how each stands, and arms them, turns one off or resets one there. Called on the show's
/// thread.
/// </summary>
public interface ITriggerControl
{
    /// <summary>How the triggers stand now.</summary>
    TriggerStates States { get; }

    /// <summary>Arms or disarms the show's triggers (arming resets the run-once ones, as on the show computer).</summary>
    string? SetArmed(bool armed);

    /// <summary>Turns one trigger off (or back on) until the show is reopened.</summary>
    /// <param name="owner">The button or timeline it's on.</param>
    /// <param name="index">Its place in that one's list of triggers.</param>
    string? SetOn(Guid owner, int index, bool on);

    /// <summary>Lets a run-once trigger that has fired fire again.</summary>
    string? Reset(Guid owner, int index);

    /// <summary>
    /// Whether a device is waiting for a trigger to learn: then commands from the show's input devices go to it (see
    /// <see cref="InputReceived"/>) instead of setting off triggers here, as they do while the show computer learns one.
    /// </summary>
    void SetDeviceLearning(bool learning);

    /// <summary>Every input event (keys, serial commands), with what came of it in a few words. On the show's thread.</summary>
    event Action<InputEvent, string>? InputReceived;
}

/// <summary>How the show's triggers stand.</summary>
/// <param name="Status">What the last press or trigger did, as the Buttons panel's footer says it.</param>
/// <param name="SerialListening">Whether a serial device is listened to, so learning can say a command will do.</param>
public sealed record TriggerStates(bool Armed, string Status, bool SerialListening, IReadOnlyList<TriggerState> Triggers);

/// <summary>One trigger: on a button or timeline (<paramref name="Owner"/>), at <paramref name="Index"/> in its list.</summary>
/// <param name="State">"Ready", "Active", "Re-arms in 2.5 s", "Off", with when it last fired.</param>
public sealed record TriggerState(Guid Owner, int Index, bool IsOn, bool IsSpent, string State);
