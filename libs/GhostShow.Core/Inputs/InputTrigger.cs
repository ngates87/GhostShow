using System.Text.Json.Serialization;

namespace GhostShow.Core.Inputs;

/// <summary>
/// An input that starts something: it presses a show button, or runs a timeline, like a prop controller's trigger
/// input. Where it comes from and which one it is (as every kind of input is described, see docs/plan-triggers.md),
/// and how it behaves, as a prop controller's trigger modes do: what it does while the input stays active, when it
/// rests, when it's set off again while still playing, and how soon it can fire again. Saved with the show (a button's
/// in show.json, a timeline's in its file).
///
/// Inputs are of two kinds. Some have states: a key is down or up, a sensor box sends <c>BTN24=On</c> and later
/// <c>BTN24=Off</c>. Those fire when they become active, and can act when they rest. Others are pulses: a command that
/// just arrives (<c>BTN24</c>), with no rest to wait for.
/// </summary>
public sealed class InputTrigger
{
    public const string Keyboard = "keyboard";

    /// <summary>A command (one line of text) from one of the show's serial devices (<see cref="Input"/>).</summary>
    public const string Serial = "serial";

    public string Source { get; set; } = Keyboard;

    /// <summary>The show's input device it listens to (<see cref="Model.Project.Inputs"/>); null for the keyboard.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? Input { get; set; }

    /// <summary>
    /// Which input of that source. Keyboard: the key's name ("F5", "D1" for the 1 key, "Q"). Serial: the command, or
    /// for a command with a value (<c>BTN24=On</c>), the part before the '=' ("BTN24").
    /// </summary>
    public string Address { get; set; } = "";

    /// <summary>
    /// Serial: the value that means active ("On" for <c>BTN24=On</c>); any other value for the same command means it's
    /// at rest. Null: every command with this name is a pulse, whatever its value.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; set; }

    /// <summary>
    /// The other way round: active when the input isn't at <see cref="Value"/>, for a normally closed sensor (its contact
    /// opens when tripped). Only for inputs with a value.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Invert { get; set; }

    /// <summary>What it does if the input is still active when what it started finishes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public WhileActive WhileActive { get; set; }

    /// <summary>What it does when the input goes back to rest (a key let go, <c>BTN24=Off</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public OnRest OnRest { get; set; }

    /// <summary>What it does if it's set off again while what it started is still playing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IfPlaying IfPlaying { get; set; }

    /// <summary>A timeline's trigger: the timeline plays in a loop until it's stopped (or its loop ended). Otherwise once.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Loop { get; set; }

    /// <summary>
    /// It fires once, then does nothing until it's reset: by <see cref="ResetBy"/>, by hand, or by arming the triggers
    /// again (a show also opens reset). For a scare that must only happen once per group.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RunOnce { get; set; }

    /// <summary>The input that resets a <see cref="RunOnce"/> trigger, e.g. a button at the room's exit; null: none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InputAddress? ResetBy { get; set; }

    /// <summary>
    /// After firing, it does nothing for this many seconds, so a flickering sensor or a crowd walking past doesn't fire
    /// it over and over. 0: it can fire again as soon as the input has rested (a pulse: at once).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double RearmSeconds { get; set; }

    /// <summary>The re-arm time starts once what it started has finished (release fade included), not when it fires.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RearmAfterTimelines { get; set; }

    /// <summary>Whether it listens to this input (any value of it).</summary>
    public bool Matches(string source, Guid? input, string address) => Source == source && Input == input && Address == address;

    /// <summary>Whether it listens to this event: the same input, and for a trigger with a value, an event with one.</summary>
    public bool Matches(InputEvent e) => Matches(e.Source, e.Input, e.Address) && (Value is null || e.Value is not null);

    /// <summary>Whether its input has states (a key, a command with a value), rather than being a pulse.</summary>
    [JsonIgnore]
    public bool HasStates => Source == Keyboard || Value is not null;

    /// <summary>What this event means for it: its input became active, went to rest, or pulsed.</summary>
    public InputLevel LevelOf(InputEvent e)
    {
        if (e.Source == Keyboard) return e.IsPressed ? InputLevel.Active : InputLevel.Rest;
        if (Value is null) return InputLevel.Pulse;
        bool active = string.Equals(e.Value, Value, StringComparison.OrdinalIgnoreCase);
        return active != Invert ? InputLevel.Active : InputLevel.Rest;
    }

    /// <summary>A copy, with its settings (for undo snapshots and tests; a duplicated button or timeline takes none).</summary>
    public InputTrigger Clone() => new()
    {
        Source = Source, Input = Input, Address = Address, Value = Value, Invert = Invert, WhileActive = WhileActive, OnRest = OnRest,
        IfPlaying = IfPlaying, Loop = Loop, RunOnce = RunOnce, ResetBy = ResetBy?.Clone(), RearmSeconds = RearmSeconds,
        RearmAfterTimelines = RearmAfterTimelines,
    };
}

/// <summary>An input on its own (a <see cref="InputTrigger.RunOnce"/> trigger's reset): where it comes from, which one, and its active value.</summary>
public sealed class InputAddress
{
    public string Source { get; set; } = InputTrigger.Keyboard;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? Input { get; set; }

    public string Address { get; set; } = "";

    /// <summary>As a trigger's: the value that sets it off; null for any.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; set; }

    /// <summary>Whether this event sets it off: a key going down, its command with its value (or any, if it has none).</summary>
    public bool IsSetOffBy(InputEvent e) =>
        e.Source == Source && e.Input == Input && e.Address == Address
        && (e.Source == InputTrigger.Keyboard ? e.IsPressed : Value is null || string.Equals(e.Value, Value, StringComparison.OrdinalIgnoreCase));

    public InputAddress Clone() => new() { Source = Source, Input = Input, Address = Address, Value = Value };
}

/// <summary>What an event means for a trigger.</summary>
public enum InputLevel
{
    /// <summary>The input became active (a key down, <c>BTN24=On</c>).</summary>
    Active,
    /// <summary>It went back to rest (a key up, <c>BTN24=Off</c>).</summary>
    Rest,
    /// <summary>A command with no states: it just arrived.</summary>
    Pulse,
}

/// <summary>If the input is still active when what the trigger started finishes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WhileActive>))]
public enum WhileActive
{
    /// <summary>Nothing: it played once (a prop controller's single shot). It fires again only once the input has rested.</summary>
    PlayOnce,
    /// <summary>It fires again, and again, for as long as the input stays active (a prop controller's loop while held).</summary>
    Repeat,
}

/// <summary>When the input goes back to rest.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OnRest>))]
public enum OnRest
{
    /// <summary>What it started carries on.</summary>
    KeepPlaying,
    /// <summary>Its timelines finish the pass they're on, then stop (End loop).</summary>
    FinishPass,
    /// <summary>Its timelines stop now, with their release fade (a momentary input: it plays only while held).</summary>
    Stop,
}

/// <summary>If it's set off again while what it started is still playing.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IfPlaying>))]
public enum IfPlaying
{
    /// <summary>It fires as a click would: a button's actions say whether a timeline plays another copy or only one; a timeline plays another copy.</summary>
    RunAgain,
    /// <summary>Nothing: it can only be set off again once what it started has finished.</summary>
    Ignore,
    /// <summary>What it started stops, and it fires again from the start.</summary>
    Restart,
    /// <summary>What it started stops, and that's all (a toggle: on, then off).</summary>
    Stop,
}
